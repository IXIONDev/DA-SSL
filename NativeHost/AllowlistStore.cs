using System.IO;
using System.Text.Json;

namespace SSLExceptionManager;

internal sealed class AllowlistStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSLExceptionManager", "approved-origins.json");

    public static string NormalizeOrigin(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("Origin format is invalid.");
        if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Only bare HTTPS origins are accepted.");
        if (Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown || uri.Host.Contains('*'))
            throw new ArgumentException("Hostname is invalid.");
        if (!uri.IsDefaultPort && (uri.Port < 1 || uri.Port > 65535))
            throw new ArgumentException("Port is invalid.");

        string host = new System.Globalization.IdnMapping().GetAscii(uri.Host).ToLowerInvariant();
        if (host.Length > 253) throw new ArgumentException("Hostname is too long.");
        return uri.IsDefaultPort ? $"https://{host}" : $"https://{host}:{uri.Port}";
    }

    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> AddAsync(string origin, CancellationToken ct = default)
    {
        string normalized = NormalizeOrigin(origin);
        await _gate.WaitAsync(ct);
        try
        {
            List<string> values = (await ReadCoreAsync(ct)).ToList();
            if (!values.Contains(normalized, StringComparer.Ordinal)) values.Add(normalized);
            await SaveCoreAsync(values, ct);
            return values;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> RemoveAsync(string origin, CancellationToken ct = default)
    {
        string normalized = NormalizeOrigin(origin);
        await _gate.WaitAsync(ct);
        try
        {
            List<string> values = (await ReadCoreAsync(ct)).Where(x => !string.Equals(x, normalized, StringComparison.Ordinal)).ToList();
            await SaveCoreAsync(values, ct);
            return values;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ContainsAsync(string origin, CancellationToken ct = default)
    {
        string normalized = NormalizeOrigin(origin);
        return (await GetAsync(ct)).Contains(normalized, StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<string>> ReadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return Array.Empty<string>();
        await using FileStream fs = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string[]? values = await JsonSerializer.DeserializeAsync<string[]>(fs, cancellationToken: ct);
        if (values is null) return Array.Empty<string>();
        return values.Select(NormalizeOrigin).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task SaveCoreAsync(IEnumerable<string> values, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        await using (FileStream fs = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(fs, values.Order(StringComparer.Ordinal).ToArray(), cancellationToken: ct);
        File.Move(temp, _path, true);
    }
}
