using System.Globalization;
using System.Threading.Channels;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

internal sealed class MessagePresentationService : IAsyncDisposable
{
    private const int QUEUE_CAPACITY = 256;
    private const int MAX_DEFAULT_WORKER_COUNT = 4;
    private readonly Channel<Action<IPinyinService>> operations = Channel.CreateBounded<Action<IPinyinService>>(new BoundedChannelOptions(QUEUE_CAPACITY)
    {
        SingleReader = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task[] workers;
    private readonly Task initialization;

    internal int WorkerCount => workers.Length;

    internal MessagePresentationService(IPinyinService pinyin) : this(() => pinyin)
    {
    }

    internal MessagePresentationService(Func<IPinyinService> pinyinFactory, int workerCount = 0)
    {
        var count = workerCount == 0 ? Math.Clamp(Environment.ProcessorCount - 1, 2, MAX_DEFAULT_WORKER_COUNT) : workerCount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var converters = Enumerable.Range(0, count).Select(_ => Task.Run(pinyinFactory)).ToArray();
        initialization = Task.WhenAll(converters);
        workers = converters.Select(RunAsync).ToArray();
    }

    internal async Task<MessagePresentationSnapshot> PrepareAsync(MessageRecord record, MessagePresentationOptions options,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var operationToken = linkedCancellation.Token;
        await initialization.WaitAsync(operationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<MessagePresentationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await operations.Writer.WriteAsync(pinyin =>
        {
            if (operationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(operationToken);
                return;
            }
            try
            {
                completion.TrySetResult(Prepare(record, options, pinyin, operationToken));
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(operationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }, operationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(operationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        operations.Writer.TryComplete();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task RunAsync(Task<IPinyinService> converter)
    {
        var pinyin = await converter.ConfigureAwait(false);
        await foreach (var operation in operations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            operation(pinyin);
        }
    }

    private static MessagePresentationSnapshot Prepare(MessageRecord record, MessagePresentationOptions options,
        IPinyinService pinyin, CancellationToken cancellationToken)
    {
        var name = options.ShowUserPinyin ? pinyin.GetPinyin(record.UserName) : "";
        if (name == record.UserName || string.IsNullOrWhiteSpace(name))
        {
            name = "";
        }
        cancellationToken.ThrowIfCancellationRequested();
        var nameAnnotations = name.Length > 0 ? pinyin.GetAnnotations(record.UserName) : [];
        var gift = record.Kind == MessageKind.Gift && options.ShowGiftPinyin ? pinyin.GetPinyin(record.GiftName) : "";
        if (gift == record.GiftName || string.IsNullOrWhiteSpace(gift))
        {
            gift = "";
        }
        cancellationToken.ThrowIfCancellationRequested();
        var contentEnabled = record.Kind switch
        {
            MessageKind.Danmu => options.ShowDanmuPinyin,
            MessageKind.SuperChat => options.ShowSuperChatPinyin,
            MessageKind.Gift => options.ShowGiftPinyin && gift.Length > 0,
            _ => false
        };
        var content = record.Kind == MessageKind.Gift
            ? string.Create(CultureInfo.GetCultureInfo(options.CultureName), $"{record.GiftName} ×{record.Quantity} · ￥{record.TotalPrice:0.##}")
            : record.Content;
        cancellationToken.ThrowIfCancellationRequested();
        var annotations = contentEnabled ? pinyin.GetAnnotations(content) : [];
        var translatable = record.Kind is MessageKind.Danmu or MessageKind.SuperChat &&
            TranslationTextExtractor.Extract(record.Content, record.Emoticons.Select(value => value.Text)).Length > 0;
        return new(record, options, name, nameAnnotations, gift, annotations, translatable);
    }
}
