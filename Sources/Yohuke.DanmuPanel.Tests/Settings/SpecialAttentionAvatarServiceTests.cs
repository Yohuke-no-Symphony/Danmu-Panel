using System.Net;
using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Workspace;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证搜索结果直接复用头像地址，并在图片损坏时回退到共享用户缓存。</summary>
public sealed class SpecialAttentionAvatarServiceTests
{
    /// <summary>搜索提供的头像不再查询用户资料，缓存复用且损坏后可回退、更新后可恢复。</summary>
    [AvaloniaFact]
    public async Task SearchPortraitUsesImageCacheAndInvalidationFallsBack()
    {
        using var directory = new MessageStoreTestDirectory();
        var requests = new List<Uri>();
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
        using var http = new HttpClient(new AvatarHttpHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));
        using var images = new AvatarCacheService(new(directory.Path), http);
        var fallback = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult("fallback.png") };
        var service = new SpecialAttentionAvatarService(images, fallback);
        var changed = new List<long>();
        void OnChanged(long uid)
        {
            changed.Add(uid);
        }
        service.AvatarChanged += OnChanged;
        service.Register(new() { UserUid = 42, AvatarUrl = "https://example.invalid/first.png" });
        var first = await service.GetAvatarAsync(42);

        Assert.True(File.Exists(first));
        Assert.Empty(fallback.RequestedUsers);
        Assert.Equal(first, await service.GetAvatarAsync(42));
        Assert.Single(requests);
        await service.InvalidateAvatarAsync(42, first);
        Assert.Equal("fallback.png", await service.GetAvatarAsync(42));
        Assert.Equal((42L, first), Assert.Single(fallback.Invalidations));
        service.Register(new() { UserUid = 42, AvatarUrl = "https://example.invalid/updated.png" });
        var updated = await service.GetAvatarAsync(42);
        Assert.NotEqual(first, updated);
        Assert.True(File.Exists(updated));
        Assert.Equal(2, requests.Count);
        fallback.NotifyChanged(73);
        Assert.Equal(new long[] { 42, 42, 73 }, changed);
        service.AvatarChanged -= OnChanged;
        Assert.Equal(0, fallback.Subscribers);
    }

    /// <summary>无搜索头像时使用共享服务，调用者取消不能转为普通占位结果。</summary>
    [AvaloniaFact]
    public async Task MissingPortraitUsesSharedAvatarAndCancellationPropagates()
    {
        using var directory = new MessageStoreTestDirectory();
        using var images = new AvatarCacheService(new(directory.Path));
        var fallback = new ControlledUserAvatarService { Resolve = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult("shared.png");
        } };
        var service = new SpecialAttentionAvatarService(images, fallback);

        Assert.Equal("shared.png", await service.GetAvatarAsync(42));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAvatarAsync(42, cancellation.Token));
    }
}
