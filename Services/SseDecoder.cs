using System.Text;

namespace AzureGptProxy.Services;

public sealed class SseDecoder
{
    private readonly StringBuilder _dataBuilder = new();

    public IEnumerable<string> PushLine(string line)
    {
        // Per SSE: empty line dispatches the event.
        if (string.IsNullOrEmpty(line))
        {
            if (_dataBuilder.Length == 0)
            {
                yield break;
            }

            var data = _dataBuilder.ToString();
            _dataBuilder.Clear();
            yield return data;
            yield break;
        }

        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            var value = line.Length >= 5 ? line[5..] : string.Empty;
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (_dataBuilder.Length > 0)
            {
                _dataBuilder.Append('\n');
            }
            _dataBuilder.Append(value);
        }
    }
}
