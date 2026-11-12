namespace Yohuke.DanmuPanel.I18n;

internal static class LocalizedExceptionText
{
    internal static TException Create<TException>(string key, Func<string, TException> create,
        params object[] arguments) where TException : Exception
    {
        string Render()
        {
            return Localization.Format(key, arguments.Select(argument =>
                argument is Func<string> provider ? provider() : argument).ToArray());
        }

        var error = create(Render());
        error.Data[typeof(LocalizedExceptionText)] = (Func<string>)Render;
        return error;
    }

    internal static string Get(Exception error)
    {
        if (error.Data[typeof(LocalizedExceptionText)] is Func<string> provider)
        {
            return provider();
        }

        return CoreExceptionInfo.TryGet(error, out var info) ? CoreErrorText.Get(info) : error.Message;
    }
}
