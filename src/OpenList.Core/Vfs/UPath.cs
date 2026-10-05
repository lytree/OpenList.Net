using System.Diagnostics.CodeAnalysis;

namespace OpenList.Vfs;

/// <summary>
/// 虚拟文件系统中的绝对路径值类型（UPath）。
/// <para>
/// 路径统一使用 <c>/</c> 作为分隔符，并始终为绝对路径：传入相对路径会被视为
/// 相对于根目录，因此 <c>new UPath("a/b")</c> 与 <c>new UPath("/a/b")</c> 等价。
/// 构造时会归一化重复斜杠、<c>.</c> 与 <c>..</c>，并去掉末尾多余斜杠。
/// </para>
/// <para>
/// 该类型是不可变值类型，支持相等比较与哈希，可作为字典键。
/// </para>
/// </summary>
public readonly struct UPath : IEquatable<UPath>, IComparable<UPath>
{
    /// <summary>规范化后的绝对路径；<c>default(UPath)</c> 时为 null。</summary>
    private readonly string? _fullName;

    /// <summary>根路径 <c>/</c>。</summary>
    public static readonly UPath Root = new("/", false);

    /// <summary>空路径（<c>default(UPath)</c> 的语义）。</summary>
    public static readonly UPath Empty = new("", false);

    /// <summary>路径分隔符。</summary>
    public const char DirectorySeparator = '/';

    /// <summary>从字符串构造路径。相对路径会被视为相对于根目录。</summary>
    /// <param name="path">路径字符串。</param>
    public UPath(string? path)
    {
        if (path is null)
        {
            _fullName = null;
            return;
        }

        if (path.Length == 0)
        {
            _fullName = string.Empty;
            return;
        }

        _fullName = Normalize(path);
    }

    /// <summary>内部构造：<paramref name="fullName"/> 已是规范化结果。</summary>
    private UPath(string? fullName, bool _)
    {
        _fullName = fullName;
    }

    /// <summary>规范化路径文本：转斜杠、去重复斜杠、解析 <c>.</c> 与 <c>..</c>、去末尾斜杠。</summary>
    private static string Normalize(string path)
    {
        var s = path.Replace('\\', '/');

        // 快速路径：已经是规范的绝对路径（以单个 '/' 开头且不含冗余内容）。
        var segments = s.Split('/');
        var stack = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            if (seg.Length == 0 || seg == ".")
            {
                // 空段（重复斜杠、末尾斜杠、开头斜杠）与 "." 均跳过。
                continue;
            }

            if (seg == "..")
            {
                // 尝试回退一级；根之上保持根（不越界）。
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(seg);
        }

        return stack.Count == 0 ? "/" : "/" + string.Join("/", stack);
    }

    /// <summary>路径是否为 null（未初始化）。</summary>
    public bool IsNull => _fullName is null;

    /// <summary>路径是否为根路径。</summary>
    public bool IsEmpty => _fullName is not null && _fullName.Length == 0;

    /// <summary>
    /// 规范化后的完整路径。
    /// <para>
    /// 仅 <c>default(UPath)</c> 为 null；任何经构造函数创建的实例（含 <c>Empty</c>）
    /// 都有确定值。调用方据此可安全解引用，使用前建议用 <see cref="IsNull"/> 判空。
    /// </para>
    /// </summary>
    [NotNull]
    public string? FullName => _fullName;

    /// <summary>路径文本；未初始化时返回空字符串。</summary>
    public override string ToString() => _fullName ?? string.Empty;

    /// <summary>路径的末级名称（文件名或目录名）。根路径返回 <c>/</c>。</summary>
    public string GetName()
    {
        if (_fullName is null) return string.Empty;
        if (_fullName == "/") return "/";
        var idx = _fullName.LastIndexOf('/');
        return idx < 0 ? _fullName : _fullName[(idx + 1)..];
    }

    /// <summary>去掉扩展名后的末级名称。</summary>
    public string GetNameWithoutExtension()
    {
        var name = GetName();
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    /// <summary>扩展名（含前导点）；无扩展名时返回空字符串。</summary>
    public string GetExtension()
    {
        var name = GetName();
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..];
    }

    /// <summary>父目录路径。根路径的父目录仍为根。</summary>
    public UPath GetDirectory()
    {
        if (_fullName is null) return Empty;
        if (_fullName == "/") return Root;
        var idx = _fullName.LastIndexOf('/');
        return idx <= 0 ? Root : new UPath(_fullName[..idx], false);
    }

    /// <summary>把子路径追加到本路径之下（等价于 <c>a / b</c>）。</summary>
    public UPath Combine(string subPath) => Combine(this, subPath);

    /// <summary>拼接两个路径：<paramref name="path"/> 之下追加 <paramref name="subPath"/>。</summary>
    public static UPath Combine(UPath path, string subPath)
    {
        if (string.IsNullOrEmpty(subPath)) return path.IsEmpty ? Root : path;
        var baseName = path.ToString();
        if (baseName.Length == 0) baseName = "/";
        return new UPath(baseName + "/" + subPath);
    }

    /// <summary>判断 <paramref name="child"/> 是否位于本路径之内（含自身）。</summary>
    public bool IsInDirectory(UPath child, bool recursive = true)
    {
        if (_fullName is null || child._fullName is null) return false;
        if (recursive)
            return child._fullName.StartsWith(_fullName == "/" ? "/" : _fullName + "/", StringComparison.Ordinal)
                || child._fullName == _fullName;

        var parent = child.GetDirectory();
        return parent._fullName == _fullName;
    }

    /// <summary>以子路径形式追加（<c>parent / child</c>）。</summary>
    public static UPath operator /(UPath path, string subPath) => Combine(path, subPath);

    /// <summary>判断路径相等。</summary>
    public static bool operator ==(UPath a, UPath b) => a.Equals(b);

    /// <summary>判断路径不等。</summary>
    public static bool operator !=(UPath a, UPath b) => !a.Equals(b);

    /// <summary>隐式转换为路径字符串。</summary>
    public static implicit operator string(UPath path) => path.ToString();

    /// <summary>
    /// 隐式从字符串构造路径，使 <c>fs.FileExists("/a/b")</c> 之类的调用可直接编译。
    /// </summary>
    public static implicit operator UPath(string path) => new(path);

    /// <summary>相等性比较（区分大小写）。</summary>
    public bool Equals(UPath other) => string.Equals(_fullName, other._fullName, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is UPath other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _fullName?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <summary>按路径字典序比较。</summary>
    public int CompareTo(UPath other) => string.CompareOrdinal(_fullName, other._fullName);
}
