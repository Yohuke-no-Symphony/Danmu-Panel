using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Xunit;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证有界表情位图的租用、淘汰、文件更新和注入生命周期。</summary>
public sealed class EmoticonBitmapCacheTests
{
    /// <summary>同图并发请求只解码一次，取消单个消费者不会取消其他租用，文件读取在后台执行。</summary>
    [AvaloniaFact]
    public async Task ConcurrentRequestsShareBackgroundDecodingAndCancelIndependently()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = CreateImage(directory.Path, "concurrent.png", 512, 512);
        var cache = new EmoticonBitmapCache();
        using var cancellation = new CancellationTokenSource();
        var uiThread = Environment.CurrentManagedThreadId;
        var cancelled = cache.AcquireAsync(path, 32, cancellation.Token);
        var requests = Enumerable.Range(0, 20).Select(_ => cache.AcquireAsync(path, 32)).ToArray();
        cancellation.Cancel();
        EmoticonBitmapLease?[] leases = [];
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            leases = await Task.WhenAll(requests);
            var image = Assert.IsType<EmoticonBitmapLease>(leases[0]).Image;
            Assert.All(leases, lease => Assert.Same(image, Assert.IsType<EmoticonBitmapLease>(lease).Image));
            Assert.Equal(1, cache.DecodeCount);
            Assert.NotEqual(uiThread, cache.LastDecodeThreadId);
            Assert.Equal(0, cache.PendingCount);
            cache.Clear();
            Assert.Equal(32, image.PixelSize.Height);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease?.Dispose();
            }
            cache.Clear();
        }
    }

    /// <summary>清理立即取消尚未交付的请求，旧结果不会进入新一代缓存，随后仍可重新载入。</summary>
    [AvaloniaFact]
    public async Task ClearingPendingLoadsCancelsTheOldGenerationAndAllowsReloading()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = CreateImage(directory.Path, "cancel.png", 512, 512);
        var cache = new EmoticonBitmapCache();
        try
        {
            var pending = cache.AcquireAsync(path, 32);
            cache.Clear();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.PendingCount);
            using var restored = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 32));
            Assert.Equal(32, restored.Image.PixelSize.Height);
            Assert.Equal(1, cache.Count);
        }
        finally
        {
            cache.Clear();
        }
    }

    /// <summary>共享位图在最后一个租用释放后仍可复用，清空缓存后才释放保留资源。</summary>
    [AvaloniaFact]
    public async Task SharedLeasesAndRetainedImagesHaveIndependentLifetimes()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = CreateImage(directory.Path, "shared.png");
        var cache = new EmoticonBitmapCache();
        try
        {
            using var first = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            using var second = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            var image = first.Image;
            Assert.Same(image, second.Image);
            first.Dispose();
            first.Dispose();
            Assert.Equal(16, second.Image.PixelSize.Height);
            second.Dispose();
            using var restored = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            Assert.Same(image, restored.Image);
            Assert.Equal(1, cache.DecodeCount);
            Assert.Equal(2, cache.HitCount);
            restored.Dispose();
            cache.Clear();
            AssertDisposed(image);
        }
        finally
        {
            cache.Clear();
        }
    }

    /// <summary>淘汰和清空仅撤销缓存保留，活动租用继续有效，缓存清空后可以继续使用。</summary>
    [AvaloniaFact]
    public async Task EvictionAndClearLeaveActiveLeasesValid()
    {
        using var directory = new MessageStoreTestDirectory();
        var firstPath = CreateImage(directory.Path, "first.png");
        var secondPath = CreateImage(directory.Path, "second.png");
        var cache = new EmoticonBitmapCache(1);
        try
        {
            using var first = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(firstPath, 16));
            using var second = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(secondPath, 16));
            var firstImage = first.Image;
            var secondImage = second.Image;
            Assert.Equal(1, cache.Count);
            Assert.Equal(16, firstImage.PixelSize.Height);
            first.Dispose();
            AssertDisposed(firstImage);
            cache.Clear();
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.RetainedBytes);
            Assert.Equal(16, secondImage.PixelSize.Height);
            using var restored = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(secondPath, 16));
            Assert.NotSame(secondImage, restored.Image);
            second.Dispose();
            AssertDisposed(secondImage);
            Assert.Equal(16, restored.Image.PixelSize.Height);
            Assert.Equal(1, cache.Count);
        }
        finally
        {
            cache.Clear();
        }
    }

    /// <summary>命中更新最近使用次序，超过条数预算时撤销最久未使用项的保留。</summary>
    [AvaloniaFact]
    public async Task EntryBudgetUsesLeastRecentlyUsedOrder()
    {
        using var directory = new MessageStoreTestDirectory();
        var paths = Enumerable.Range(0, 3).Select(index => CreateImage(directory.Path, index + ".png")).ToArray();
        var cache = new EmoticonBitmapCache(2);
        try
        {
            using var first = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(paths[0], 16));
            using var second = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(paths[1], 16));
            var secondImage = second.Image;
            second.Dispose();
            using var touched = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(paths[0], 16));
            using var third = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(paths[2], 16));
            AssertDisposed(secondImage);
            Assert.Equal(2, cache.Count);
            using var retained = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(paths[0], 16));
            Assert.Same(first.Image, retained.Image);
            Assert.Equal(3, cache.DecodeCount);
        }
        finally
        {
            cache.Clear();
        }
    }

    /// <summary>字节预算使用解码后的像素成本，压缩文件很小也不能突破保留预算。</summary>
    [AvaloniaFact]
    public async Task DecodedPixelBytesLimitRetentionIndependentlyOfEntryCount()
    {
        using var directory = new MessageStoreTestDirectory();
        var paths = Enumerable.Range(0, 3).Select(index => CreateImage(directory.Path, index + ".png")).ToArray();
        var cache = new EmoticonBitmapCache(128, 16 * 16 * 4);
        try
        {
            foreach (var path in paths)
            {
                Assert.True(new FileInfo(path).Length < 16 * 16 * 4);
                using var lease = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
                Assert.Equal(1, cache.Count);
                Assert.Equal(16 * 16 * 4, cache.RetainedBytes);
                Assert.Equal(16, lease.Image.PixelSize.Height);
            }
        }
        finally
        {
            cache.Clear();
        }
    }

    /// <summary>禁用保留或图片超过预算时仍返回可用租用，释放后立即归还位图。</summary>
    [AvaloniaTheory]
    [InlineData(0, 0)]
    [InlineData(128, 1)]
    public async Task UnretainedImagesStayAliveUntilTheirLeaseIsReleased(int maxEntries, long maxBytes)
    {
        using var directory = new MessageStoreTestDirectory();
        var path = CreateImage(directory.Path, "large.png");
        var cache = new EmoticonBitmapCache(maxEntries, maxBytes);
        using var lease = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
        var image = lease.Image;
        Assert.Equal(16, image.PixelSize.Height);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.RetainedBytes);
        cache.Clear();
        Assert.Equal(16, image.PixelSize.Height);
        lease.Dispose();
        AssertDisposed(image);
    }

    /// <summary>不同解码高度使用独立位图，相同尺寸才复用已有结果。</summary>
    [AvaloniaFact]
    public async Task DecodeHeightSeparatesCachedImages()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = CreateImage(directory.Path, "sizes.png");
        var cache = new EmoticonBitmapCache();
        try
        {
            using var small = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            using var large = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 32));
            using var restored = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            Assert.Equal(16, small.Image.PixelSize.Height);
            Assert.Equal(32, large.Image.PixelSize.Height);
            Assert.NotSame(small.Image, large.Image);
            Assert.Same(small.Image, restored.Image);
            Assert.Equal(2, cache.DecodeCount);
        }
        finally
        {
            cache.Clear();
        }
    }

    /// <summary>同路径图片替换生成新缓存项，旧租用释放不能损坏新版本的保留和租用。</summary>
    [AvaloniaFact]
    public async Task ReplacingAFileDoesNotInvalidateAnExistingLease()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = CreateImage(directory.Path, "replace.png");
        var cache = new EmoticonBitmapCache();
        try
        {
            using var original = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            var oldImage = original.Image;
            CreateImage(directory.Path, "replace.png", 32, 16);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            using var replacement = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            Assert.NotSame(oldImage, replacement.Image);
            Assert.Equal(16, oldImage.PixelSize.Width);
            Assert.Equal(32, replacement.Image.PixelSize.Width);
            Assert.Equal(1, cache.Count);
            original.Dispose();
            AssertDisposed(oldImage);
            using var restored = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            Assert.Same(replacement.Image, restored.Image);
            Assert.Equal(2, cache.DecodeCount);
        }
        finally
        {
            cache.Clear();
        }
    }

    /// <summary>文件删除或损坏后不返回旧图、不保留失败，文件恢复后允许重新读取。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptFilesCanRecoverWithoutReturningStaleImages(bool corrupt)
    {
        using var directory = new MessageStoreTestDirectory();
        var path = CreateImage(directory.Path, "recover.png");
        var cache = new EmoticonBitmapCache();
        try
        {
            using var original = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            var oldImage = original.Image;
            original.Dispose();
            if (corrupt)
            {
                File.WriteAllText(path, "invalid image");
            }
            else
            {
                File.Delete(path);
            }
            Assert.Null(await cache.AcquireAsync(path, 16));
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.RetainedBytes);
            AssertDisposed(oldImage);
            CreateImage(directory.Path, "recover.png");
            using var recovered = Assert.IsType<EmoticonBitmapLease>(await cache.AcquireAsync(path, 16));
            Assert.NotSame(oldImage, recovered.Image);
            Assert.Equal(16, recovered.Image.PixelSize.Height);
        }
        finally
        {
            cache.Clear();
        }
    }

    /// <summary>注入缓存改变时换用新租用，隐藏分支只释放资源而不触发解码。</summary>
    [AvaloniaFact]
    public async Task AssignedCacheChangesRespectVisibilityAndReleaseOldLeases()
    {
        using var directory = new MessageStoreTestDirectory();
        var path = CreateImage(directory.Path, "inherit.png");
        var firstCache = new EmoticonBitmapCache();
        var secondCache = new EmoticonBitmapCache();
        var text = new EmoticonTextBlock
        {
            ContentText = "[笑]",
            BitmapCache = firstCache,
            Emoticons = [new DanmuEmoticon { Text = "[笑]", ImagePath = path }]
        };
        var host = new Border { Child = text };
        var window = new Window { Width = 360, Height = 180, Content = host };
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var original = Image(text);
            text.BitmapCache = secondCache;
            await EmoticonImageTestHelper.LoadAsync(window);
            var replacement = Image(text);
            Assert.NotSame(original, replacement);
            firstCache.Clear();
            AssertDisposed(original);
            Assert.Equal(28, replacement.PixelSize.Height);
            host.IsVisible = false;
            text.BitmapCache = firstCache;
            Assert.Equal(1, firstCache.DecodeCount);
            secondCache.Clear();
            AssertDisposed(replacement);
            host.IsVisible = true;
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.Equal(28, Image(text).PixelSize.Height);
            Assert.Equal(2, firstCache.DecodeCount);
        }
        finally
        {
            window.Close();
            firstCache.Clear();
            secondCache.Clear();
        }
    }

    private static string CreateImage(string directory, string name, int width = 16, int height = 16)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        using var bitmap = new RenderTargetBitmap(new(width, height));
        bitmap.Save(path);
        return path;
    }

    private static Bitmap Image(EmoticonTextBlock text)
    {
        return Assert.IsType<Bitmap>(Assert.IsType<Image>(Assert.Single(text.Inlines!
            .OfType<InlineUIContainer>()).Child).Source);
    }

    private static void AssertDisposed(Bitmap image)
    {
        var exception = Record.Exception(() => _ = image.PixelSize);
        Assert.True(exception is NullReferenceException or ObjectDisposedException);
    }
}
