// Pickle value model used by the read-only Ren'Py save parser.
//
// SECURITY: this model is *data only*. Nothing in this file (or anywhere else under
// Galbox.Core/Saves) ever resolves a type, instantiates a class or invokes a callable.
// Objects that a normal unpickler would construct are represented as PickleOpaque
// records that merely *describe* what the pickle asked for.
using System.Runtime.CompilerServices;

namespace Galbox.Core.Saves.Pickle;

/// <summary>Kind of value held by a <see cref="PickleValue"/>.</summary>
public enum PickleValueKind
{
    /// <summary>Python <c>None</c>.</summary>
    None,

    /// <summary>Python <c>bool</c>.</summary>
    Bool,

    /// <summary>Python <c>int</c>/<c>long</c>.</summary>
    Int,

    /// <summary>Python <c>float</c>.</summary>
    Float,

    /// <summary>Python <c>str</c> (Py2 bytes) or <c>unicode</c>.</summary>
    String,

    /// <summary>Python <c>bytes</c> / <c>bytearray</c>.</summary>
    Bytes,

    /// <summary>Python <c>list</c> (or list subclass).</summary>
    List,

    /// <summary>Python <c>tuple</c>.</summary>
    Tuple,

    /// <summary>Python <c>dict</c> (or dict subclass).</summary>
    Dictionary,

    /// <summary>Python <c>set</c>.</summary>
    Set,

    /// <summary>Python <c>frozenset</c>.</summary>
    FrozenSet,

    /// <summary>
    /// A value the pickle would have produced by constructing an object (REDUCE / BUILD /
    /// INST / OBJ / NEWOBJ / GLOBAL / extension registry / PERSID). Never constructed here.
    /// </summary>
    Opaque,
}

/// <summary>How an opaque value came into existence inside the pickle stream.</summary>
public enum PickleConstructionKind
{
    /// <summary>A <c>GLOBAL</c>/<c>STACK_GLOBAL</c> opcode referencing module:name.</summary>
    Global,

    /// <summary>A <c>REDUCE</c> opcode (callable + args). Not executed.</summary>
    Reduce,

    /// <summary>An <c>INST</c> opcode.</summary>
    Instance,

    /// <summary>An <c>OBJ</c> opcode.</summary>
    Object,

    /// <summary>A <c>NEWOBJ</c>/<c>NEWOBJ_EX</c> opcode.</summary>
    NewObject,

    /// <summary>An <c>EXT1</c>/<c>EXT2</c>/<c>EXT4</c> extension-registry code.</summary>
    Extension,

    /// <summary>A <c>PERSID</c>/<c>BINPERSID</c> persistent id (no callback is invoked).</summary>
    PersistentId,
}

/// <summary>
/// Description of an object a normal unpickler would have constructed. The scanner records
/// <see cref="ModuleName"/>/<see cref="TypeName"/> as plain strings and never resolves them.
/// </summary>
public sealed class PickleOpaque
{
    internal PickleOpaque(PickleConstructionKind kind, string moduleName, string typeName, int opcodeOffset)
    {
        Kind = kind;
        ModuleName = moduleName;
        TypeName = typeName;
        OpcodeOffset = opcodeOffset;
    }

    /// <summary>Which opcode family produced this placeholder.</summary>
    public PickleConstructionKind Kind { get; }

    /// <summary>Module part of the referenced global (empty for pure REDUCE results).</summary>
    public string ModuleName { get; }

    /// <summary>Type / attribute name part of the referenced global.</summary>
    public string TypeName { get; }

    /// <summary>Byte offset of the opcode that created this placeholder (audit evidence).</summary>
    public int OpcodeOffset { get; }

    /// <summary>The callable that a real unpickler would have invoked (never invoked).</summary>
    public PickleValue? Callable { get; internal set; }

    /// <summary>Positional arguments that a real unpickler would have passed (never passed).</summary>
    public PickleValue? Arguments { get; internal set; }

    /// <summary>State applied by <c>BUILD</c>. Captured as data, never passed to <c>__setstate__</c>.</summary>
    public PickleValue? State { get; internal set; }

    /// <summary>Items appended by <c>APPEND</c>/<c>APPENDS</c>.</summary>
    public List<PickleValue> Appended { get; } = new();

    /// <summary>Items applied by <c>SETITEM</c>/<c>SETITEMS</c>.</summary>
    public List<KeyValuePair<PickleValue, PickleValue>> Items { get; } = new();

    /// <summary>Human readable <c>module.name</c> (or the referenced name alone).</summary>
    public string DisplayName => ModuleName.Length == 0 ? TypeName : ModuleName + "." + TypeName;

    /// <inheritdoc />
    public override string ToString() => "<" + DisplayName + ">";
}

/// <summary>
/// An immutable-by-convention node of the scanned pickle graph. Containers hold references to
/// child <see cref="PickleValue"/> instances (the memo table preserves object identity, so the
/// graph stays a DAG and is never deep-copied).
/// </summary>
public sealed class PickleValue
{
    private static readonly PickleValue NoneValue = new(PickleValueKind.None);
    private static readonly PickleValue TrueValue = new(PickleValueKind.Bool) { Bool = true };
    private static readonly PickleValue FalseValue = new(PickleValueKind.Bool) { Bool = false };

    private PickleValue(PickleValueKind kind) => Kind = kind;

    /// <summary>What this node represents.</summary>
    public PickleValueKind Kind { get; }

    /// <summary>Decoded text for <see cref="PickleValueKind.String"/>.</summary>
    public string? Text { get; private init; }

    /// <summary>Original bytes for string/bytes nodes.</summary>
    public byte[]? RawBytes { get; private init; }

    /// <summary>Integer payload.</summary>
    public long Integer { get; private init; }

    /// <summary>Floating point payload.</summary>
    public double Float { get; private init; }

    /// <summary>Boolean payload.</summary>
    public bool Bool { get; private init; }

    /// <summary>Elements of a list/tuple/set, in stream order.</summary>
    public List<PickleValue> Items { get; } = new();

    /// <summary>Key/value pairs of a dictionary, in stream order.</summary>
    public List<KeyValuePair<PickleValue, PickleValue>> Pairs { get; } = new();

    /// <summary>Placeholder description when <see cref="Kind"/> is <see cref="PickleValueKind.Opaque"/>.</summary>
    public PickleOpaque? Opaque { get; private init; }

    /// <summary>
    /// State handed to a <c>BUILD</c> opcode for a value that is <em>not</em> an opaque object
    /// (rare: a plain container carrying a state dict). Captured as data only — no
    /// <c>__setstate__</c> is ever invoked.
    /// </summary>
    public PickleValue? AttachedState { get; internal set; }

    /// <summary>Number of elements (containers) or 0.</summary>
    public int Count => Kind switch
    {
        PickleValueKind.List or PickleValueKind.Tuple or PickleValueKind.Set or PickleValueKind.FrozenSet => Items.Count,
        PickleValueKind.Dictionary => Pairs.Count,
        PickleValueKind.Opaque => Opaque!.State?.Pairs.Count ?? Opaque.Appended.Count + Opaque.Items.Count,
        _ => 0,
    };

    internal static PickleValue MakeNone() => NoneValue;

    internal static PickleValue MakeBool(bool value) => value ? TrueValue : FalseValue;

    internal static PickleValue MakeInt(long value) => new(PickleValueKind.Int) { Integer = value };

    internal static PickleValue MakeFloat(double value) => new(PickleValueKind.Float) { Float = value };

    internal static PickleValue MakeBytes(byte[] value) => new(PickleValueKind.Bytes) { RawBytes = value };

    internal static PickleValue MakeString(string text, byte[]? raw)
        => new(PickleValueKind.String) { Text = text, RawBytes = raw };

    internal static PickleValue MakeContainer(PickleValueKind kind)
        => new(kind);

    internal static PickleValue MakeOpaque(PickleOpaque opaque)
        => new(PickleValueKind.Opaque) { Opaque = opaque };

    /// <summary>
    /// Looks a member up on a dictionary or on an opaque object's captured BUILD state.
    /// Returns <see langword="null"/> when absent. Never throws.
    /// </summary>
    public PickleValue? GetMember(string name)
        => TryGetMember(name, out var value) ? value : null;

    /// <summary>Non-throwing member lookup; see <see cref="GetMember"/>.</summary>
    public bool TryGetMember(string name, out PickleValue value)
    {
        value = NoneValue;

        if (Kind == PickleValueKind.Dictionary)
        {
            foreach (var pair in Pairs)
            {
                if (pair.Key.Kind == PickleValueKind.String && pair.Key.Text == name)
                {
                    value = pair.Value;
                    return true;
                }
            }

            return false;
        }

        if (Kind != PickleValueKind.Opaque)
        {
            if (AttachedState is { } attached && attached.TryGetMember(name, out value))
            {
                return true;
            }

            return false;
        }

        if (Opaque!.State is { } state)
        {
            if (state.TryGetMember(name, out value))
            {
                return true;
            }
        }

        foreach (var pair in Opaque.Items)
        {
            if (pair.Key.Kind == PickleValueKind.String && pair.Key.Text == name)
            {
                value = pair.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a string member (dictionary key or opaque state field).</summary>
    public string? GetString(string name)
    {
        var v = GetMember(name);
        return v is { Kind: PickleValueKind.String } ? v.Text : null;
    }

    /// <summary>Reads an integer member.</summary>
    public long? GetInt(string name)
    {
        var v = GetMember(name);
        return v is { Kind: PickleValueKind.Int } ? v.Integer : null;
    }

    /// <summary>Reads a float member (integers are widened).</summary>
    public double? GetDouble(string name)
    {
        var v = GetMember(name);
        return v?.Kind switch
        {
            PickleValueKind.Float => v.Float,
            PickleValueKind.Int => v.Integer,
            _ => null,
        };
    }

    /// <summary>Member lookup that keeps walking a dotted path, e.g. <c>"voice.tlid"</c>.</summary>
    public PickleValue? GetPath(string dottedPath)
    {
        PickleValue? current = this;

        foreach (var part in dottedPath.Split('.'))
        {
            if (current is null)
            {
                return null;
            }

            if (!current.TryGetMember(part, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Dotted-path lookup returning a string (e.g. <c>"voice.tlid"</c>).</summary>
    public string? GetPathString(string dottedPath)
        => GetPath(dottedPath) is { Kind: PickleValueKind.String } s ? s.Text : null;

    /// <summary>
    /// Enumerates the sequence form of this value: list/tuple/set elements, or the items an
    /// opaque object accumulated through APPEND/APPENDS/SETITEMS.
    /// </summary>
    public IEnumerable<PickleValue> EnumerateSequence()
    {
        switch (Kind)
        {
            case PickleValueKind.List:
            case PickleValueKind.Tuple:
            case PickleValueKind.Set:
            case PickleValueKind.FrozenSet:
                foreach (var item in Items)
                {
                    yield return item;
                }

                break;

            case PickleValueKind.Opaque:
                foreach (var item in Opaque!.Appended)
                {
                    yield return item;
                }

                foreach (var pair in Opaque.Items)
                {
                    yield return pair.Value;
                }

                break;
        }
    }

    /// <summary>
    /// Enumerates key/value pairs of a dictionary, or the state dictionary capture of an opaque
    /// object. This is what lets the reader walk <c>renpy.persistent.Persistent</c> fields.
    /// </summary>
    public IEnumerable<KeyValuePair<PickleValue, PickleValue>> EnumeratePairs()
    {
        if (Kind == PickleValueKind.Dictionary)
        {
            foreach (var pair in Pairs)
            {
                yield return pair;
            }

            yield break;
        }

        if (Kind != PickleValueKind.Opaque)
        {
            if (AttachedState is { } attached)
            {
                foreach (var pair in attached.EnumeratePairs())
                {
                    yield return pair;
                }
            }

            yield break;
        }

        if (Opaque!.State is { } state)
        {
            foreach (var pair in state.EnumeratePairs())
            {
                yield return pair;
            }
        }

        foreach (var pair in Opaque.Items)
        {
            yield return pair;
        }
    }

    /// <summary>Creates a 1..n element tuple from the supplied values.</summary>
    public static PickleValue MakeTuple(params PickleValue[] values)
    {
        var tuple = MakeContainer(PickleValueKind.Tuple);
        tuple.Items.AddRange(values);
        return tuple;
    }

    /// <summary>Renders a short, non-recursive description for diagnostics.</summary>
    public override string ToString() => Kind switch
    {
        PickleValueKind.None => "None",
        PickleValueKind.Bool => Bool ? "True" : "False",
        PickleValueKind.Int => Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PickleValueKind.Float => Float.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PickleValueKind.String => "\"" + Text + "\"",
        PickleValueKind.Bytes => "<bytes:" + (RawBytes?.Length ?? 0) + ">",
        PickleValueKind.Opaque => Opaque!.ToString()!,
        _ => "<" + Kind.ToString().ToLowerInvariant() + ":" + Count + ">",
    };
}

/// <summary>
/// Structural equality over <see cref="PickleValue"/>. Needed because pickle dictionary keys may
/// themselves be tuples (e.g. <c>('0101',)</c> in <c>persistent._seen_images</c>). Opaque nodes
/// compare by reference — they are never value-equal to anything but themselves.
/// </summary>
public sealed class PickleValueStructuralComparer : IEqualityComparer<PickleValue>
{
    /// <summary>Shared instance.</summary>
    public static readonly PickleValueStructuralComparer Instance = new();

    /// <inheritdoc />
    public bool Equals(PickleValue? x, PickleValue? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null || x.Kind != y.Kind)
        {
            return false;
        }

        switch (x.Kind)
        {
            case PickleValueKind.None:
                return true;
            case PickleValueKind.Bool:
                return x.Bool == y.Bool;
            case PickleValueKind.Int:
                return x.Integer == y.Integer;
            case PickleValueKind.Float:
                return x.Float.Equals(y.Float);
            case PickleValueKind.String:
                return string.Equals(x.Text, y.Text, StringComparison.Ordinal);
            case PickleValueKind.Bytes:
                return x.RawBytes.AsSpan().SequenceEqual(y.RawBytes);
            case PickleValueKind.List:
            case PickleValueKind.Tuple:
            case PickleValueKind.Set:
            case PickleValueKind.FrozenSet:
                if (x.Items.Count != y.Items.Count)
                {
                    return false;
                }

                if (x.Kind is PickleValueKind.Set or PickleValueKind.FrozenSet)
                {
                    // Sets are unordered; compare as multisets via the hash set.
                    var set = new HashSet<PickleValue>(y.Items, this);
                    foreach (var item in x.Items)
                    {
                        if (!set.Contains(item))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                for (var i = 0; i < x.Items.Count; i++)
                {
                    if (!Equals(x.Items[i], y.Items[i]))
                    {
                        return false;
                    }
                }

                return true;
            case PickleValueKind.Dictionary:
                if (x.Pairs.Count != y.Pairs.Count)
                {
                    return false;
                }

                foreach (var pair in x.Pairs)
                {
                    var found = false;
                    foreach (var other in y.Pairs)
                    {
                        if (Equals(pair.Key, other.Key) && Equals(pair.Value, other.Value))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }
                }

                return true;
            default:
                return ReferenceEquals(x.Opaque, y.Opaque);
        }
    }

    /// <inheritdoc />
    public int GetHashCode(PickleValue obj)
    {
        var hash = new HashCode();
        hash.Add((int)obj.Kind);

        switch (obj.Kind)
        {
            case PickleValueKind.Bool:
                hash.Add(obj.Bool);
                break;
            case PickleValueKind.Int:
                hash.Add(obj.Integer);
                break;
            case PickleValueKind.Float:
                hash.Add(obj.Float);
                break;
            case PickleValueKind.String:
                hash.Add(obj.Text, StringComparer.Ordinal);
                break;
            case PickleValueKind.Bytes:
                hash.AddBytes(obj.RawBytes ?? Array.Empty<byte>());
                break;
            case PickleValueKind.List:
            case PickleValueKind.Tuple:
                foreach (var item in obj.Items)
                {
                    hash.Add(GetHashCode(item));
                }

                break;
            case PickleValueKind.Set:
            case PickleValueKind.FrozenSet:
                // Order independent: XOR element hashes.
                var acc = 0;
                foreach (var item in obj.Items)
                {
                    acc ^= GetHashCode(item);
                }

                hash.Add(acc);
                break;
            case PickleValueKind.Dictionary:
                var pairAcc = 0;
                foreach (var pair in obj.Pairs)
                {
                    pairAcc ^= GetHashCode(pair.Key) ^ GetHashCode(pair.Value);
                }

                hash.Add(pairAcc);
                break;
            default:
                hash.Add(RuntimeHelpers.GetHashCode(obj.Opaque!));
                break;
        }

        return hash.ToHashCode();
    }
}
