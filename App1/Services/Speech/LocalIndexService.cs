using Anfeta.UI.Models.Weblab;
using System;
using System.Collections.Generic;
using Anfeta.UI.Services.Speech;
using System.Linq;
using System.IO;

public sealed class LocalIndexService
{
    private readonly object _gate = new();
    private List<SearchResultRow> _items = new();
    private long _version;

    public long Version
    {
        get { lock (_gate) return _version; }
    }

    public bool HasData { get { lock (_gate) return _items.Count > 0; } }
    public int Count { get { lock (_gate) return _items.Count; } }

    public IReadOnlyList<SearchResultRow> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }

    public void Set(List<SearchResultRow> items)
    {
        if (items == null || items.Count == 0)
            throw new InvalidOperationException("Refusing to set empty index.");

        lock (_gate)
        {
            _items = items;
            _version++;
        }
    }

    public bool RemoveExact(string fullPath)
    {
        var norm = Norm(fullPath);
        lock (_gate)
        {
            var before = _items.Count;
            _items.RemoveAll(x => Norm(x.FullPath) == norm);
            var changed = _items.Count != before;
            if (changed) _version++;
            return changed;
        }
    }

    public int RemovePrefix(string folderPath)
    {
        var prefix = EnsureDirPrefix(folderPath);
        lock (_gate)
        {
            var before = _items.Count;
            _items.RemoveAll(x => Norm(x.FullPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var removed = before - _items.Count;
            if (removed > 0) _version++;
            return removed;
        }
    }

    public int RenameExact(string oldPath, string newPath, bool isFolder)
    {
        var oldN = Norm(oldPath);
        var newN = Norm(newPath);

        lock (_gate)
        {
            var hit = _items.FirstOrDefault(x => Norm(x.FullPath) == oldN);
            if (hit == null) return 0;

            hit.FullPath = newN;
            hit.Name = Path.GetFileName(newN);
            hit.Type = isFolder ? "FOLDER" : "FILE";
            _version++;
            return 1;
        }
    }

    public int RenamePrefix(string oldFolder, string newFolder)
    {
        var oldFolderN = Norm(oldFolder);
        var newFolderN = Norm(newFolder);

        var oldPrefix = EnsureDirPrefix(oldFolderN);
        var newPrefix = EnsureDirPrefix(newFolderN);

        lock (_gate)
        {
            int changed = 0;

            foreach (var it in _items)
            {
                var p = Norm(it.Target); // ✅ usa Target (tu ruta real)

                // ✅ 1) Caso exacto: la carpeta raíz guardada SIN '\'
                if (string.Equals(p, oldFolderN, StringComparison.OrdinalIgnoreCase))
                {
                    it.Target = newFolderN;
                    it.Name = Path.GetFileName(newFolderN);
                    it.Type = "FOLDER";
                    changed++;
                    continue;
                }

                // ✅ 2) Caso hijos: todo lo que cuelga de esa carpeta
                if (!p.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rest = p.Substring(oldPrefix.Length);
                var updated = newPrefix + rest;

                it.Target = updated;
                it.Name = Path.GetFileName(updated);
                changed++;
            }

            if (changed > 0) _version++;
            return changed;
        }
    }

    private static string Norm(string p) => (p ?? "").Trim().Replace('/', '\\');

    private static string EnsureDirPrefix(string folder)
    {
        var p = Norm(folder);
        if (!p.EndsWith("\\", StringComparison.Ordinal)) p += "\\";
        return p;
    }
    public void Clear()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
                return;

            _items.Clear();
            _version++;
        }
    }

    public List<SearchResultRow> GetAll()
    {
        lock (_gate) return _items.ToList();
    }
}