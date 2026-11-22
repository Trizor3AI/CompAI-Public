using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

// One reader per bridge process. Retain bounded read-ahead between frames and
// reject an oversized frame before building its string or parsing its JSON.
internal sealed class CompanionLineReader(TextReader reader)
{
    // Preserve the existing UTF-16 length contract, including JSON escapes.
    // ASCII JSON escaping can use 12 characters for one non-BMP character.
    internal const int MaximumCharacters = 131072;
    internal const int BufferCharacters = 4096;
    private readonly char[] buffer = new char[BufferCharacters];
    private int position;
    private int available;
    private bool skipLineFeed;

    internal async ValueTask<string?> ReadLineAsync(CancellationToken token)
    {
        StringBuilder? partial = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (this.position == this.available)
            {
                this.available = await reader.ReadAsync(this.buffer.AsMemory(), token);
                this.position = 0;
                if (this.available == 0) return partial?.ToString();
            }
            if (this.skipLineFeed)
            {
                this.skipLineFeed = false;
                if (this.buffer[this.position] == '\n') this.position++;
                if (this.position == this.available) continue;
            }

            var remaining = this.buffer.AsSpan(this.position, this.available - this.position);
            var terminator = remaining.IndexOfAny('\r', '\n');
            var length = terminator < 0 ? remaining.Length : terminator;
            if (length > MaximumCharacters - (partial?.Length ?? 0))
                throw new InvalidDataException("Companion message exceeded size limit");

            if (terminator >= 0)
            {
                var line = partial is null ? new string(this.buffer, this.position, length) :
                    partial.Append(this.buffer, this.position, length).ToString();
                this.position += length;
                this.skipLineFeed = this.buffer[this.position++] == '\r';
                return line;
            }
            partial ??= new StringBuilder(BufferCharacters, MaximumCharacters);
            partial.Append(this.buffer, this.position, length);
            this.position += length;
        }
    }
}
