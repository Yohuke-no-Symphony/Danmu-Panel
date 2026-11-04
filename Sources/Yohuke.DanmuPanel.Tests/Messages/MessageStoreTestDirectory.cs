using System;
using System.IO;

namespace Yohuke.DanmuPanel.Tests.Messages;

internal sealed class MessageStoreTestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "danmu-panel-message-tests", Guid.NewGuid().ToString("N"));
    internal string DatabasePath => System.IO.Path.Combine(Path, "messages.db");

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}
