using System;
using System.Collections.Generic;

namespace Blackbird.ViewModels;

/// <summary>
/// The build output shown in the log pane. Appends are streamed to the view as
/// chunks so the cost of showing a line never depends on how long the log is.
/// Lives on the UI thread.
/// </summary>
public sealed class BuildLog
{
    // Immutable chunks, so a snapshot is a copy of references and the text itself can be joined
    // off the UI thread (saving a multi-MB log at build end never stalls a frame).
    private List<string> _chunks = [];
    private int _length;
    private string? _snapshot;

    /// <summary>Raised with each appended chunk. A chunk may end mid-line.</summary>
    public event Action<string>? Appended;

    /// <summary>Raised when the whole log is replaced (cleared or loaded from disk).</summary>
    public event Action? Replaced;

    public int Length => _length;

    /// <summary>True while a saved log is being read, so the view can stay blank instead of showing the empty state.</summary>
    public bool IsLoading { get; private set; }

    public string Text
    {
        get
        {
            if (_snapshot is null)
            {
                _snapshot = string.Concat(_chunks);
                _chunks = [_snapshot];
            }

            return _snapshot;
        }
    }

    /// <summary>The log as it stands, cheap to take on the UI thread; join it with string.Concat anywhere.</summary>
    public string[] Snapshot() => [.. _chunks];

    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        _chunks.Add(chunk);
        _length += chunk.Length;
        _snapshot = null;
        IsLoading = false;
        Appended?.Invoke(chunk);
    }

    public void Replace(string text = "", bool isLoading = false)
    {
        _chunks = text.Length == 0 ? [] : [text];
        _length = text.Length;
        _snapshot = text;
        IsLoading = isLoading;
        Replaced?.Invoke();
    }
}
