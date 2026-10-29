using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.Delivery.Engine.Downloads;

/// <summary>
/// The name of the file a download writes a record to (osdu/docs/reference/flow/download.md, File names): the record's id,
/// with the two characters an id may hold that no file name carries everywhere written as two characters no id holds, so the
/// name reads as the id, turns back into it, and no two ids share it. An OSDU id is letters, digits, <c>_</c>, <c>-</c>,
/// <c>.</c>, and from its third segment <c>:</c> and <c>%</c> (osdu/specs/core/storage/openapi.yaml, the record id pattern):
/// <c>:</c> is refused by Windows and read as a URI scheme by Hadoop and Spark, and <c>%</c> is the escape of a URL, which a
/// blob's address is. So <c>:</c> is written <c>~</c> and <c>%</c> is written <c>=</c>.
/// <para>
/// A name that cannot be the id itself, because it would be too long, the id holds a character outside the pattern, or another
/// id of the download already has it but for its letters' case (OSDU ids are case-sensitive, and the folders of Windows and
/// macOS are not), is the id's name cut to fit, then <c>@</c> and the start of the id's SHA-256: a name holding <c>@</c> is one
/// no id turns back from, and the record's own <c>id</c> in the file says which it is.
/// </para>
/// </summary>
public static class DownloadFileNames
{
    /// <summary>The extension of every file: the record is JSON.</summary>
    public const string Extension = ".json";

    /// <summary>
    /// The longest name, extension included: below the 255 characters NTFS, APFS and ext4 take, with room for the
    /// <c>.tmp</c> a local write writes beside the file first.
    /// </summary>
    public const int MaxLength = Data.DeliveryModel.MaxDownloadFileNameLength;

    /// <summary>What a <c>:</c> of the id is written as.</summary>
    public const char Colon = '~';

    /// <summary>What a <c>%</c> of the id is written as.</summary>
    public const char Percent = '=';

    /// <summary>What starts the hash of a name that is not the id's own.</summary>
    public const char Marker = '@';

    /// <summary>How many hex digits of the id's SHA-256 a name that is not the id's own carries.</summary>
    public const int HashDigits = 12;

    /// <summary>
    /// The name of <paramref name="id"/>'s file: the id with <c>:</c> written <c>~</c> and <c>%</c> written <c>=</c>, and the
    /// extension; null when the id cannot be named so (a character outside the id pattern, or a name longer than
    /// <see cref="MaxLength"/>), and the file takes <see cref="Distinct"/>.
    /// </summary>
    public static string? Of(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length + Extension.Length > MaxLength)
        {
            return null;
        }

        var name = new StringBuilder(id.Length + Extension.Length);
        foreach (var c in id)
        {
            switch (c)
            {
                case ':':
                    name.Append(Colon);
                    break;
                case '%':
                    name.Append(Percent);
                    break;
                default:
                    if (!IsPlain(c))
                    {
                        return null;
                    }

                    name.Append(c);
                    break;
            }
        }

        return name.Append(Extension).ToString();
    }

    /// <summary>
    /// The name of <paramref name="id"/>'s file when <see cref="Of"/> gives none, or another id holds it but for its case: the
    /// id written as <see cref="Of"/> writes it, a character outside the id pattern as <c>_</c>, cut to fit, then
    /// <c>@</c>, the first <see cref="HashDigits"/> hex digits of the SHA-256 of the id, and the extension.
    /// </summary>
    public static string Distinct(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..HashDigits];
        var room = MaxLength - Extension.Length - 1 - HashDigits;
        var name = new StringBuilder(MaxLength);
        foreach (var c in id)
        {
            if (name.Length == room)
            {
                break;
            }

            name.Append(c switch
            {
                ':' => Colon,
                '%' => Percent,
                _ => IsPlain(c) ? c : '_',
            });
        }

        return name.Append(Marker).Append(hash).Append(Extension).ToString();
    }

    /// <summary>
    /// The id <paramref name="fileName"/> is the name of, when it is an id's own (<see cref="Of"/>); null for a name that is
    /// not, which only the record in the file, or the download's ledger, says the id of.
    /// </summary>
    public static string? IdOf(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!fileName.EndsWith(Extension, StringComparison.Ordinal) || fileName.Contains(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        var stem = fileName[..^Extension.Length];
        if (stem.Length == 0 || stem.Any(c => c != Colon && c != Percent && !IsPlain(c)))
        {
            return null;
        }

        return stem.Replace(Colon, ':').Replace(Percent, '%');
    }

    /// <summary>Whether <paramref name="c"/> is written in a name as it is: an ASCII letter or digit, <c>_</c>, <c>-</c> or <c>.</c>.</summary>
    private static bool IsPlain(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';
}
