// Audit trail produced by the read-only pickle scanner.
//
// The audit is the security evidence: it records every object the pickle *asked* to construct
// (with the byte offset of the responsible opcode) without ever constructing anything.
namespace Galbox.Core.Saves.Pickle;

/// <summary>One "the pickle wanted to construct this" event.</summary>
/// <param name="OpcodeOffset">Byte offset of the opcode that requested the construction.</param>
/// <param name="Kind">Opcode family that requested it.</param>
/// <param name="ModuleName">Referenced module (empty when not applicable).</param>
/// <param name="TypeName">Referenced type / attribute name.</param>
/// <param name="Classification">Threat classification used for reporting only.</param>
public readonly record struct PickleConstructionRecord(
    int OpcodeOffset,
    PickleConstructionKind Kind,
    string ModuleName,
    string TypeName,
    PickleThreatClassification Classification)
{
    /// <summary>Human readable <c>module.name</c>.</summary>
    public string DisplayName => ModuleName.Length == 0 ? TypeName : ModuleName + "." + TypeName;

    /// <inheritdoc />
    public override string ToString() => $"@0x{OpcodeOffset:X} {Kind} {DisplayName} [{Classification}]";
}

/// <summary>
/// Report-only classification of a construction request. It never influences control flow: the
/// scanner does not call anything regardless of the classification.
/// </summary>
public enum PickleThreatClassification
{
    /// <summary>A harmless container/builtin such as <c>__builtin__.dict</c>.</summary>
    BenignBuiltin,

    /// <summary><c>copy_reg._reconstructor</c> / <c>__newobj__</c> — pure object plumbing.</summary>
    ReconstructorHelper,

    /// <summary>A game/engine class reference (Ren'Py classes, store.* classes).</summary>
    ApplicationClass,

    /// <summary>An unknown global that would have been imported by a real unpickler.</summary>
    UnknownGlobal,

    /// <summary>A global belonging to a known code-execution family (os/subprocess/...).</summary>
    DangerousGlobal,
}

/// <summary>Severity of a scanner diagnostic entry.</summary>
public enum PickleScanSeverity
{
    /// <summary>Informational note.</summary>
    Info,

    /// <summary>Something unusual that callers may want to surface.</summary>
    Warning,
}

/// <summary>A single diagnostic emitted while walking the stream.</summary>
/// <param name="Severity">Severity.</param>
/// <param name="Offset">Byte offset the diagnostic refers to.</param>
/// <param name="Message">Human readable message (English; comments-only policy).</param>
public readonly record struct PickleScanDiagnostic(PickleScanSeverity Severity, int Offset, string Message);

/// <summary>Everything the scanner observed, exposed as raw evidence.</summary>
public sealed class PickleScanAudit
{
    internal PickleScanAudit()
    {
    }

    /// <summary>Protocol version declared by the <c>PROTO</c> opcode (0 when absent).</summary>
    public int Protocol { get; internal set; }

    /// <summary>Total number of opcodes consumed.</summary>
    public int OpcodeCount { get; internal set; }

    /// <summary>Number of bytes consumed up to and including <c>STOP</c>.</summary>
    public int BytesConsumed { get; internal set; }

    /// <summary>Total payload byte length that was offered to the scanner.</summary>
    public int InputLength { get; internal set; }

    /// <summary>Peak stack depth reached.</summary>
    public int MaxStackDepth { get; internal set; }

    /// <summary>Number of memo entries materialised.</summary>
    public int MemoEntries { get; internal set; }

    /// <summary>Whether the stream terminated with <c>STOP</c>.</summary>
    public bool SawStop { get; internal set; }

    /// <summary>Every construction request, in stream order.</summary>
    public List<PickleConstructionRecord> Constructions { get; } = new();

    /// <summary>Diagnostics (non-fatal oddities).</summary>
    public List<PickleScanDiagnostic> Diagnostics { get; } = new();

    /// <summary>Distinct <c>module.name</c> globals referenced by the stream, sorted.</summary>
    public IReadOnlyList<string> DistinctGlobals
        => Constructions
            .Where(c => c.Kind == PickleConstructionKind.Global)
            .Select(c => c.DisplayName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Distinct construction kinds observed.</summary>
    public IReadOnlyList<PickleConstructionKind> ConstructionKinds
        => Constructions.Select(c => c.Kind).Distinct().OrderBy(k => k).ToList();

    /// <summary>Construction requests classified as <see cref="PickleThreatClassification.DangerousGlobal"/>.</summary>
    public IReadOnlyList<PickleConstructionRecord> DangerousConstructions
        => Constructions.Where(c => c.Classification == PickleThreatClassification.DangerousGlobal).ToList();

    /// <summary>True when the stream asked for something in a code-execution family.</summary>
    public bool RequestedCodeExecution => DangerousConstructions.Count > 0;
}

/// <summary>Resource limits applied while scanning untrusted input.</summary>
public sealed record PickleScanOptions
{
    /// <summary>Default limits: generous for real Ren'Py saves, bounded against hostile input.</summary>
    public static readonly PickleScanOptions Default = new();

    /// <summary>Maximum number of opcodes to execute.</summary>
    public int MaxOpcodes { get; init; } = 20_000_000;

    /// <summary>Maximum stack depth.</summary>
    public int MaxStackDepth { get; init; } = 4_000_000;

    /// <summary>Maximum number of elements a single container may accumulate.</summary>
    public int MaxContainerItems { get; init; } = 20_000_000;

    /// <summary>Byte length of string/bytes payloads that are normalised into arrays.</summary>
    public int MaxBytesAllocation { get; init; } = 64 * 1024 * 1024;
}

/// <summary>Thrown when untrusted pickle bytes cannot be scanned safely.</summary>
public sealed class PickleScanException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PickleScanException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public PickleScanException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Result of a scan: the reconstructed data graph plus the audit evidence.</summary>
/// <param name="Root">The top-level value.</param>
/// <param name="Audit">The audit trail.</param>
public sealed record PickleScanResult(PickleValue Root, PickleScanAudit Audit);
