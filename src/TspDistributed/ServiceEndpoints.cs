namespace TspDistributed;

public static class ServiceEndpoints
{
    public static IReadOnlyList<Uri> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<Uri>();
        foreach (var line in text.Split('\n').Select(x => x.Trim()).Where(x => x.Length != 0))
        {
            string value = line.Contains("://", StringComparison.Ordinal) ? line : "http://" + line;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw new ArgumentException($"Некорректный адрес сервиса: {line}");
            string authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
            int colon = authority.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(authority[(colon + 1)..], out int port) || port is < 1 or > 65535 ||
                (authority.StartsWith('[') ? authority.IndexOf(']') != colon - 1 : authority[..colon].Contains(':')))
                throw new ArgumentException($"Укажите порт сервиса: {line}");
            if (result.Contains(uri)) throw new ArgumentException($"Адрес повторяется: {line}");
            result.Add(uri);
        }
        if (result.Count is < 1 or > 16) throw new ArgumentException("Нужно указать от 1 до 16 сервисов.");
        return result;
    }
}
