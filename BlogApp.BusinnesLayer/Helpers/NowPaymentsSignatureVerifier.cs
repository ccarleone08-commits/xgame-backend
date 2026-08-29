using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlogApp.BusinnesLayer.Helpers;

public static class NowPaymentsSignatureVerifier
{
    public static bool Verify(string rawBody, string receivedSignature, string ipnSecret)
    {
        if (string.IsNullOrWhiteSpace(rawBody) ||
            string.IsNullOrWhiteSpace(receivedSignature) ||
            string.IsNullOrWhiteSpace(ipnSecret))
            return false;

        object? sorted;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            sorted = SortElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }

        var canonical = JsonSerializer.Serialize(sorted);

        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(ipnSecret.Trim()));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(receivedSignature.ToLowerInvariant()));
    }

    private static object? SortElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .OrderBy(p => p.Name)
                .ToDictionary(p => p.Name, p => SortElement(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(SortElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetDecimal(out var value) ? value : element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
