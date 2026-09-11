// Zero-execution pickle scanner.
//
// ============================ SECURITY MODEL ============================
// A pickle stream is a program.  REDUCE / INST / OBJ / NEWOBJ / GLOBAL opcodes make a normal
// unpickler import modules and call arbitrary callables, which is remote-code-execution by
// design.  Ren'Py saves are untrusted input, so this scanner never does any of that.
//
// The whole class is a *byte-stream decoder* that produces a data tree:
//   * GLOBAL / STACK_GLOBAL  -> read module + name, push a PickleOpaque that merely *stores*
//                               the two strings.  No Assembly.Load / Type.GetType happens.
//   * REDUCE / INST / OBJ / NEWOBJ / NEWOBJ_EX
//                            -> pop the callable and its arguments and push a PickleOpaque.
//                               Nothing is invoked.
//   * BUILD                  -> pop the state and *attach it as data* to the target node.
//                               No __setstate__ / __dict__.update is performed.
//   * EXT1/2/4               -> record the registry code as an opaque.  No registry lookup.
//   * PERSID / BINPERSID     -> record the id as a value.  No persistent_load callback.
//
// There is deliberately no reflection, no Activator.CreateInstance, no dynamic, no delegate
// invocation and no Type.GetType anywhere in this file — the capability to execute simply is
// not present.  See RenpyPickleSafetyTests in the verification harness for a behavioural proof:
// a payload whose execution would drop a marker file on disk is fed to this scanner, and the
// file demonstrably never appears.
// ========================================================================
using System.Buffers.Binary;
using System.Text;

namespace Galbox.Core.Saves.Pickle;

/// <summary>
/// Read-only, zero-execution decoder for Python pickle byte streams (protocols 0-5).
/// </summary>
public static class PickleScanner
{
    /// <summary>
    /// Decodes <paramref name="data"/> into a <see cref="PickleScanResult"/> without ever
    /// importing, instantiating or invoking anything referenced by the stream.
    /// </summary>
    /// <param name="data">Raw pickle bytes (untrusted).</param>
    /// <param name="options">Optional resource limits.</param>
    /// <exception cref="PickleScanException">The stream is malformed or exceeds a limit.</exception>
    public static PickleScanResult Scan(byte[] data, PickleScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PickleScannerEngine(data, options ?? PickleScanOptions.Default).Run();
    }

    /// <summary>Marker pushed by the MARK opcode.</summary>
    private sealed class Mark
    {
        internal static readonly Mark Instance = new();

        private Mark()
        {
        }
    }

    private sealed class PickleScannerEngine
    {
        private readonly byte[] _data;
        private readonly PickleScanOptions _options;
        private readonly PickleScanAudit _audit = new();
        private readonly List<object> _stack = new();
        private readonly Dictionary<long, PickleValue> _memo = new();
        private int _pos;
        private long _nextMemoIndex;

        internal PickleScannerEngine(byte[] data, PickleScanOptions options)
        {
            _data = data;
            _options = options;
            _audit.InputLength = data.Length;
        }

        internal PickleScanResult Run()
        {
            PickleValue? root = null;

            while (true)
            {
                if (_pos >= _data.Length)
                {
                    throw new PickleScanException(
                        $"Pickle stream ended without a STOP opcode at offset 0x{_pos:X}.");
                }

                if (_audit.OpcodeCount >= _options.MaxOpcodes)
                {
                    throw new PickleScanException($"Pickle exceeds the opcode limit ({_options.MaxOpcodes}).");
                }

                var opcodeOffset = _pos;
                var opcode = _data[_pos++];
                _audit.OpcodeCount++;

                switch (opcode)
                {
                    // ---------------------------------------------------------- framing
                    case 0x80: // PROTO
                        _audit.Protocol = _data[_pos++];
                        break;

                    case 0x95: // FRAME
                        _ = ReadInt64LittleEndian();
                        break;

                    case 0x2E: // STOP
                        _audit.SawStop = true;
                        _audit.BytesConsumed = _pos;
                        _audit.MemoEntries = _memo.Count;
                        root = _stack.Count > 0 ? Pop() : PickleValue.MakeNone();
                        return new PickleScanResult(root, _audit);

                    // ---------------------------------------------------------- stack ops
                    case 0x28: // MARK
                        Push(Mark.Instance);
                        break;

                    case 0x30: // POP
                        Pop();
                        break;

                    case 0x31: // POP_MARK
                        PopMark(discard: true);
                        break;

                    case 0x32: // DUP
                        Push(Peek());
                        break;

                    // ---------------------------------------------------------- simple values
                    case 0x4E: // NONE
                        Push(PickleValue.MakeNone());
                        break;

                    case 0x88: // NEWTRUE
                        Push(PickleValue.MakeBool(true));
                        break;

                    case 0x89: // NEWFALSE
                        Push(PickleValue.MakeBool(false));
                        break;

                    case 0x4B: // BININT1
                        Push(PickleValue.MakeInt(ReadByte()));
                        break;

                    case 0x4D: // BININT2
                        Push(PickleValue.MakeInt(ReadUInt16LittleEndian()));
                        break;

                    case 0x4A: // BININT
                        Push(PickleValue.MakeInt(ReadInt32LittleEndian()));
                        break;

                    case 0x49: // INT  (also encodes booleans as 00/01)
                        {
                            var text = ReadLineAscii();
                            if (text == "00")
                            {
                                Push(PickleValue.MakeBool(false));
                            }
                            else if (text == "01")
                            {
                                Push(PickleValue.MakeBool(true));
                            }
                            else
                            {
                                Push(PickleValue.MakeInt(ParseLong(text)));
                            }

                            break;
                        }

                    case 0x4C: // LONG
                        {
                            var text = ReadLineAscii().TrimEnd('L');
                            Push(PickleValue.MakeInt(ParseLong(text)));
                            break;
                        }

                    case 0x8A: // LONG1
                        Push(PickleValue.MakeInt(ReadLittleEndianTwosComplement(ReadByte())));
                        break;

                    case 0x8B: // LONG4
                        Push(PickleValue.MakeInt(ReadLittleEndianTwosComplement(CheckedLength(ReadInt32LittleEndian()))));
                        break;

                    case 0x47: // BINFLOAT (big endian)
                        {
                            var bytes = ReadRaw(8);
                            Push(PickleValue.MakeFloat(BinaryPrimitives.ReadDoubleBigEndian(bytes)));
                            break;
                        }

                    case 0x46: // FLOAT
                        Push(PickleValue.MakeFloat(double.Parse(ReadLineAscii(),
                            System.Globalization.CultureInfo.InvariantCulture)));
                        break;

                    // ---------------------------------------------------------- strings
                    case 0x55: // SHORT_BINSTRING
                        Push(MakePy2String(ReadRaw(ReadByte())));
                        break;

                    case 0x54: // BINSTRING
                        Push(MakePy2String(ReadRaw(CheckedLength(ReadInt32LittleEndian()))));
                        break;

                    case 0x53: // STRING
                        Push(MakePy2String(DecodeQuotedString(ReadLineBytes())));
                        break;

                    case 0x58: // BINUNICODE
                        Push(PickleValue.MakeString(
                            DecodeUtf8(ReadRaw(CheckedLength(ReadInt32LittleEndian()))), null));
                        break;

                    case 0x8C: // SHORT_BINUNICODE
                        Push(PickleValue.MakeString(DecodeUtf8(ReadRaw(ReadByte())), null));
                        break;

                    case 0x8D: // BINUNICODE8
                        Push(PickleValue.MakeString(
                            DecodeUtf8(ReadRaw(CheckedLength(ReadInt64LittleEndian()))), null));
                        break;

                    case 0x56: // UNICODE (raw-unicode-escape, newline terminated)
                        Push(PickleValue.MakeString(DecodeRawUnicodeEscape(ReadLineBytes()), null));
                        break;

                    case 0x42: // BINBYTES
                        Push(PickleValue.MakeBytes(ReadRaw(CheckedLength(ReadInt32LittleEndian()))));
                        break;

                    case 0x43: // SHORT_BINBYTES
                        Push(PickleValue.MakeBytes(ReadRaw(ReadByte())));
                        break;

                    case 0x8E: // BINBYTES8
                        Push(PickleValue.MakeBytes(ReadRaw(CheckedLength(ReadInt64LittleEndian()))));
                        break;

                    case 0x96: // BYTEARRAY8
                        Push(PickleValue.MakeBytes(ReadRaw(CheckedLength(ReadInt64LittleEndian()))));
                        break;

                    // ---------------------------------------------------------- containers
                    case 0x5D: // EMPTY_LIST
                        Push(PickleValue.MakeContainer(PickleValueKind.List));
                        break;

                    case 0x7D: // EMPTY_DICT
                        Push(PickleValue.MakeContainer(PickleValueKind.Dictionary));
                        break;

                    case 0x29: // EMPTY_TUPLE
                        Push(PickleValue.MakeContainer(PickleValueKind.Tuple));
                        break;

                    case 0x8F: // EMPTY_SET
                        Push(PickleValue.MakeContainer(PickleValueKind.Set));
                        break;

                    case 0x6C: // LIST
                        PushContainer(PickleValueKind.List, PopMark(discard: false));
                        break;

                    case 0x74: // TUPLE
                        PushContainer(PickleValueKind.Tuple, PopMark(discard: false));
                        break;

                    case 0x64: // DICT
                        PushDictionary(PopMark(discard: false));
                        break;

                    case 0x91: // FROZENSET
                        PushContainer(PickleValueKind.FrozenSet, PopMark(discard: false));
                        break;

                    case 0x85: // TUPLE1
                        PushContainer(PickleValueKind.Tuple, new List<PickleValue> { Pop() });
                        break;

                    case 0x86: // TUPLE2
                        {
                            var second = Pop();
                            var first = Pop();
                            PushContainer(PickleValueKind.Tuple, new List<PickleValue> { first, second });
                            break;
                        }

                    case 0x87: // TUPLE3
                        {
                            var third = Pop();
                            var second = Pop();
                            var first = Pop();
                            PushContainer(PickleValueKind.Tuple, new List<PickleValue> { first, second, third });
                            break;
                        }

                    // Note the evaluation order: the mark/value must be popped BEFORE the
                    // container is peeked at, because the container sits *below* the mark.
                    case 0x61: // APPEND
                        {
                            var item = Pop();
                            AppendTo(Peek(), item);
                            break;
                        }

                    case 0x65: // APPENDS
                        {
                            var items = PopMark(discard: false);
                            AppendMany(Peek(), items);
                            break;
                        }

                    case 0x73: // SETITEM
                        {
                            var value = Pop();
                            var key = Pop();
                            SetItem(Peek(), key, value);
                            break;
                        }

                    case 0x75: // SETITEMS
                        {
                            var items = PopMark(discard: false);
                            SetItems(Peek(), items);
                            break;
                        }

                    case 0x90: // ADDITEMS
                        {
                            var items = PopMark(discard: false);
                            AddAll(Peek(), items);
                            break;
                        }

                    // ---------------------------------------------------------- memo
                    case 0x70: // PUT
                        MemoPut(ParseLong(ReadLineAscii()), Peek());
                        break;

                    case 0x67: // GET
                        Push(MemoGet(ParseLong(ReadLineAscii()), opcodeOffset));
                        break;

                    case 0x71: // BINPUT
                        MemoPut(ReadByte(), Peek());
                        break;

                    case 0x68: // BINGET
                        Push(MemoGet(ReadByte(), opcodeOffset));
                        break;

                    case 0x72: // LONG_BINPUT
                        MemoPut(ReadUInt32LittleEndian(), Peek());
                        break;

                    case 0x6A: // LONG_BINGET
                        Push(MemoGet(ReadUInt32LittleEndian(), opcodeOffset));
                        break;

                    case 0x94: // MEMOIZE
                        MemoPut(_nextMemoIndex, Peek());
                        break;

                    // ---------------------------------------------------------- construction (never executed)
                    case 0x63: // GLOBAL
                        {
                            var module = ReadLineAscii();
                            var name = ReadLineAscii();
                            PushConstruction(PickleConstructionKind.Global, module, name, opcodeOffset,
                                classification: ClassifyGlobal(module, name));
                            break;
                        }

                    case 0x93: // STACK_GLOBAL
                        {
                            var name = AsName(Pop());
                            var module = AsName(Pop());
                            PushConstruction(PickleConstructionKind.Global, module, name, opcodeOffset,
                                classification: ClassifyGlobal(module, name));
                            break;
                        }

                    case 0x52: // REDUCE  -- callable + args, NOT invoked
                        {
                            var args = Pop();
                            var callable = Peek();
                            var op = new PickleOpaque(
                                PickleConstructionKind.Reduce,
                                callable.Opaque?.ModuleName ?? string.Empty,
                                callable.Opaque?.TypeName ?? callable.ToString() ?? "?",
                                opcodeOffset)
                            {
                                Callable = callable,
                                Arguments = args,
                            };

                            var record = new PickleConstructionRecord(
                                opcodeOffset,
                                PickleConstructionKind.Reduce,
                                op.ModuleName,
                                op.TypeName,
                                callable.Opaque?.Classification() ?? PickleThreatClassification.UnknownGlobal);
                            _audit.Constructions.Add(record);
                            ReplaceTop(PickleValue.MakeOpaque(op));
                            break;
                        }

                    case 0x69: // INST -- module/name + mark args, NOT invoked
                        {
                            var module = ReadLineAscii();
                            var name = ReadLineAscii();
                            var args = PopMark(discard: false);
                            var op = new PickleOpaque(PickleConstructionKind.Instance, module, name, opcodeOffset)
                            {
                                Arguments = PickleValue.MakeTuple(args.ToArray()),
                            };
                            _audit.Constructions.Add(new PickleConstructionRecord(
                                opcodeOffset, PickleConstructionKind.Instance, module, name,
                                ClassifyGlobal(module, name)));
                            Push(PickleValue.MakeOpaque(op));
                            break;
                        }

                    case 0x6F: // OBJ -- mark args + TOS class, NOT invoked
                        {
                            var args = PopMark(discard: false);
                            var cls = Pop();
                            var op = new PickleOpaque(
                                PickleConstructionKind.Object,
                                cls.Opaque?.ModuleName ?? string.Empty,
                                cls.Opaque?.TypeName ?? "?",
                                opcodeOffset)
                            {
                                Callable = cls,
                                Arguments = PickleValue.MakeTuple(args.ToArray()),
                            };
                            _audit.Constructions.Add(new PickleConstructionRecord(
                                opcodeOffset, PickleConstructionKind.Object, op.ModuleName, op.TypeName,
                                cls.Opaque?.Classification() ?? PickleThreatClassification.UnknownGlobal));
                            Push(PickleValue.MakeOpaque(op));
                            break;
                        }

                    case 0x81: // NEWOBJ -- args + class, NOT invoked
                        {
                            var args = Pop();
                            var cls = Pop();
                            PushNewObject(PickleConstructionKind.NewObject, cls, args, null, opcodeOffset);
                            break;
                        }

                    case 0x92: // NEWOBJ_EX -- kwargs + args + class, NOT invoked
                        {
                            var kwargs = Pop();
                            var args = Pop();
                            var cls = Pop();
                            PushNewObject(PickleConstructionKind.NewObject, cls, args, kwargs, opcodeOffset);
                            break;
                        }

                    case 0x62: // BUILD -- state is captured as data, never applied
                        {
                            var state = Pop();
                            var target = Peek();

                            if (target.Kind == PickleValueKind.Opaque)
                            {
                                target.Opaque!.State = state;
                            }
                            else if (target.Kind == PickleValueKind.Dictionary && state.Kind == PickleValueKind.Dictionary)
                            {
                                // dict subclass semantics: merge the state pairs into the mapping.
                                SetItems(target, state.Pairs);
                            }
                            else
                            {
                                target.AttachedState = state;
                                _audit.Diagnostics.Add(new PickleScanDiagnostic(
                                    PickleScanSeverity.Info, opcodeOffset,
                                    "BUILD state captured on a non-object value (" + target.Kind + ")."));
                            }

                            break;
                        }

                    case 0x82: // EXT1
                        PushExtension(ReadByte(), opcodeOffset);
                        break;

                    case 0x83: // EXT2
                        PushExtension(ReadUInt16LittleEndian(), opcodeOffset);
                        break;

                    case 0x84: // EXT4
                        PushExtension(ReadUInt32LittleEndian(), opcodeOffset);
                        break;

                    case 0x50: // PERSID -- no persistent_load callback is invoked
                        PushConstruction(PickleConstructionKind.PersistentId, string.Empty,
                            ReadLineAscii(), opcodeOffset, PickleThreatClassification.ApplicationClass);
                        break;

                    case 0x51: // BINPERSID
                        {
                            var id = Pop();
                            PushConstruction(PickleConstructionKind.PersistentId, string.Empty,
                                id.ToString() ?? "?", opcodeOffset,
                                PickleThreatClassification.ApplicationClass);
                            break;
                        }

                    default:
                        throw new PickleScanException(
                            $"Unsupported pickle opcode 0x{opcode:X2} at offset 0x{opcodeOffset:X}.");
                }

                if (_stack.Count > _audit.MaxStackDepth)
                {
                    _audit.MaxStackDepth = _stack.Count;
                }

                if (_stack.Count > _options.MaxStackDepth)
                {
                    throw new PickleScanException($"Pickle exceeds the stack depth limit ({_options.MaxStackDepth}).");
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private void PushNewObject(
            PickleConstructionKind kind, PickleValue cls, PickleValue args, PickleValue? kwargs, int opcodeOffset)
        {
            var op = new PickleOpaque(
                kind,
                cls.Opaque?.ModuleName ?? string.Empty,
                cls.Opaque?.TypeName ?? "?",
                opcodeOffset)
            {
                Callable = cls,
                Arguments = args,
            };

            if (kwargs is not null)
            {
                op.Items.Add(new KeyValuePair<PickleValue, PickleValue>(
                    PickleValue.MakeString("__kwargs__", null), kwargs));
            }

            _audit.Constructions.Add(new PickleConstructionRecord(
                opcodeOffset, kind, op.ModuleName, op.TypeName,
                cls.Opaque?.Classification() ?? PickleThreatClassification.UnknownGlobal));
            Push(PickleValue.MakeOpaque(op));
        }

        private void PushConstruction(
            PickleConstructionKind kind,
            string module,
            string name,
            int opcodeOffset,
            PickleThreatClassification classification)
        {
            _audit.Constructions.Add(new PickleConstructionRecord(opcodeOffset, kind, module, name, classification));
            Push(PickleValue.MakeOpaque(new PickleOpaque(kind, module, name, opcodeOffset)));
        }

        private void PushExtension(long code, int opcodeOffset)
        {
            _audit.Constructions.Add(new PickleConstructionRecord(
                opcodeOffset, PickleConstructionKind.Extension, "copyreg._extension_cache",
                code.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PickleThreatClassification.ReconstructorHelper));
            Push(PickleValue.MakeOpaque(new PickleOpaque(
                PickleConstructionKind.Extension, "copyreg._extension_cache",
                code.ToString(System.Globalization.CultureInfo.InvariantCulture), opcodeOffset)));
        }

        /// <summary>
        /// Classifies a referenced global for *reporting only*. The result never influences
        /// whether anything is resolved or called, because nothing ever is.
        /// </summary>
        private static PickleThreatClassification ClassifyGlobal(string module, string name)
            => PickleThreatClassifier.Classify(module, name);

        private static string AsName(PickleValue value) => value.Kind switch
        {
            PickleValueKind.String => value.Text ?? string.Empty,
            PickleValueKind.Opaque => value.Opaque!.DisplayName,
            _ => value.ToString() ?? string.Empty,
        };

        private void EnsureItemBudget(int count, int offset)
        {
            if (count > _options.MaxContainerItems)
            {
                throw new PickleScanException(
                    $"Container at 0x{offset:X} exceeds the item limit ({_options.MaxContainerItems}).");
            }
        }

        private void PushContainer(PickleValueKind kind, List<PickleValue> items)
        {
            EnsureItemBudget(items.Count, _pos);
            var container = PickleValue.MakeContainer(kind);
            container.Items.AddRange(items);
            Push(container);
        }

        private void PushDictionary(List<PickleValue> flatItems)
        {
            if (flatItems.Count % 2 != 0)
            {
                throw new PickleScanException($"DICT at 0x{_pos:X} has an odd number of stack items.");
            }

            EnsureItemBudget(flatItems.Count, _pos);
            var dict = PickleValue.MakeContainer(PickleValueKind.Dictionary);

            for (var i = 0; i < flatItems.Count; i += 2)
            {
                dict.Pairs.Add(new KeyValuePair<PickleValue, PickleValue>(flatItems[i], flatItems[i + 1]));
            }

            Push(dict);
        }

        private void AppendTo(PickleValue target, PickleValue item)
        {
            EnsureItemBudget(target.Count + 1, _pos);

            switch (target.Kind)
            {
                case PickleValueKind.List:
                case PickleValueKind.Set:
                case PickleValueKind.FrozenSet:
                case PickleValueKind.Tuple:
                    target.Items.Add(item);
                    break;
                case PickleValueKind.Opaque:
                    target.Opaque!.Appended.Add(item);
                    break;
                case PickleValueKind.Dictionary:
                    // dict.append is not a Python thing; record as a positional entry.
                    target.Pairs.Add(new KeyValuePair<PickleValue, PickleValue>(
                        PickleValue.MakeInt(target.Pairs.Count), item));
                    break;
                default:
                    _audit.Diagnostics.Add(new PickleScanDiagnostic(
                        PickleScanSeverity.Warning, _pos, "APPEND against a non-container value."));
                    break;
            }
        }

        private void AppendAll(PickleValue target, List<PickleValue> items) => AppendMany(target, items);
        private void AppendMany(PickleValue target, List<PickleValue> items)
        {
            EnsureItemBudget(target.Count + items.Count, _pos);

            foreach (var item in items)
            {
                AppendTo(target, item);
            }
        }

        private void AddAll(PickleValue target, List<PickleValue> items)
        {
            if (target.Kind is PickleValueKind.Set or PickleValueKind.FrozenSet)
            {
                foreach (var item in items)
                {
                    if (!target.Items.Contains(item, PickleValueStructuralComparer.Instance))
                    {
                        target.Items.Add(item);
                    }
                }

                return;
            }

            AppendMany(target, items);
        }

        private void SetItem(PickleValue target, PickleValue key, PickleValue value)
        {
            EnsureItemBudget(target.Pairs.Count + 1, _pos);

            if (target.Kind == PickleValueKind.Opaque)
            {
                target.Opaque!.Items.Add(new KeyValuePair<PickleValue, PickleValue>(key, value));
                return;
            }

            if (target.Kind != PickleValueKind.Dictionary)
            {
                _audit.Diagnostics.Add(new PickleScanDiagnostic(
                    PickleScanSeverity.Warning, _pos, "SETITEM against a non-mapping value (" + target.Kind + ")."));
                return;
            }

            target.Pairs.Add(new KeyValuePair<PickleValue, PickleValue>(key, value));
        }

        private void SetItems(PickleValue target, List<KeyValuePair<PickleValue, PickleValue>> pairs)
        {
            EnsureItemBudget(target.Pairs.Count + pairs.Count, _pos);

            foreach (var pair in pairs)
            {
                SetItem(target, pair.Key, pair.Value);
            }
        }

        private void SetItems(PickleValue target, List<PickleValue> flatItems)
        {
            if (flatItems.Count % 2 != 0)
            {
                throw new PickleScanException($"SETITEMS at 0x{_pos:X} has an odd number of stack items.");
            }

            for (var i = 0; i < flatItems.Count; i += 2)
            {
                SetItem(target, flatItems[i], flatItems[i + 1]);
            }
        }

        // ------------------------------------------------------------------ stack plumbing

        private void Push(PickleValue value) => _stack.Add(value);

        private void Push(Mark mark) => _stack.Add(mark);

        private PickleValue Peek() => _stack.Count == 0
            ? throw new PickleScanException($"Stack underflow at offset 0x{_pos:X}.")
            : _stack[^1] as PickleValue
              ?? throw new PickleScanException($"Top of stack is a MARK at offset 0x{_pos:X}.");

        private PickleValue Pop()
        {
            if (_stack.Count == 0)
            {
                throw new PickleScanException($"Stack underflow at offset 0x{_pos:X}.");
            }

            var top = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);

            return top as PickleValue
                ?? throw new PickleScanException($"Popped a MARK value at offset 0x{_pos:X}.");
        }

        private void ReplaceTop(PickleValue value) => _stack[^1] = value;

        private List<PickleValue> PopMark(bool discard)
        {
            var markIndex = _stack.Count - 1;

            while (markIndex >= 0 && !ReferenceEquals(_stack[markIndex], Mark.Instance))
            {
                markIndex--;
            }

            if (markIndex < 0)
            {
                throw new PickleScanException($"No MARK found on the stack at offset 0x{_pos:X}.");
            }

            var result = new List<PickleValue>(_stack.Count - markIndex - 1);

            if (!discard)
            {
                for (var i = markIndex + 1; i < _stack.Count; i++)
                {
                    if (_stack[i] is PickleValue value)
                    {
                        result.Add(value);
                    }
                }
            }

            _stack.RemoveRange(markIndex, _stack.Count - markIndex);
            return result;
        }

        private void MemoPut(long index, PickleValue value)
        {
            if (index < 0 || index > _options.MaxContainerItems)
            {
                throw new PickleScanException($"Memo index {index} is out of range at 0x{_pos:X}.");
            }

            _memo[index] = value;

            if (index >= _nextMemoIndex)
            {
                _nextMemoIndex = index + 1;
            }
        }

        private PickleValue MemoGet(long index, int opcodeOffset)
            => _memo.TryGetValue(index, out var value)
                ? value
                : throw new PickleScanException($"Memo index {index} is empty at 0x{opcodeOffset:X}.");

        // ------------------------------------------------------------------ raw readers

        private byte ReadByte() => _pos < _data.Length
            ? _data[_pos++]
            : throw new PickleScanException($"Unexpected end of stream at 0x{_pos:X}.");

        private byte[] ReadRaw(int length)
        {
            if (length < 0)
            {
                throw new PickleScanException($"Negative length {length} at 0x{_pos:X}.");
            }

            if (length > _options.MaxBytesAllocation)
            {
                throw new PickleScanException(
                    $"Payload length {length} exceeds the allocation limit at 0x{_pos:X}.");
            }

            if (_pos + (long)length > _data.Length)
            {
                throw new PickleScanException(
                    $"Payload of {length} bytes at 0x{_pos:X} runs past the end of the stream.");
            }

            var slice = new byte[length];
            Buffer.BlockCopy(_data, _pos, slice, 0, length);
            _pos += length;
            return slice;
        }

        private int CheckedLength(int length) => length;

        private int CheckedLength(long length) => length is < 0 or > int.MaxValue
            ? throw new PickleScanException($"Length {length} is out of range at 0x{_pos:X}.")
            : (int)length;

        private ushort ReadUInt16LittleEndian() => BinaryPrimitives.ReadUInt16LittleEndian(ReadRaw(2));

        private uint ReadUInt32LittleEndian() => BinaryPrimitives.ReadUInt32LittleEndian(ReadRaw(4));

        private int ReadInt32LittleEndian() => BinaryPrimitives.ReadInt32LittleEndian(ReadRaw(4));

        private long ReadInt64LittleEndian() => BinaryPrimitives.ReadInt64LittleEndian(ReadRaw(8));

        private string ReadLineAscii() => Encoding.ASCII.GetString(ReadLineBytes());

        private byte[] ReadLineBytes()
        {
            var start = _pos;

            while (_pos < _data.Length && _data[_pos] != (byte)'\n')
            {
                _pos++;
            }

            if (_pos >= _data.Length)
            {
                throw new PickleScanException($"Unterminated line at 0x{start:X}.");
            }

            var length = _pos - start;
            var slice = new byte[length];
            Buffer.BlockCopy(_data, start, slice, 0, length);
            _pos++; // consume '\n'
            return slice;
        }

        private long ReadLittleEndianTwosComplement(int byteCount)
        {
            var raw = ReadRaw(byteCount);

            if (raw.Length == 0)
            {
                return 0;
            }

            long value = 0;

            for (var i = raw.Length - 1; i >= 0; i--)
            {
                value = (value << 8) | raw[i];
            }

            if ((raw[^1] & 0x80) != 0 && raw.Length < 8)
            {
                value -= 1L << (raw.Length * 8);
            }

            return value;
        }

        // ------------------------------------------------------------------ text decoding

        /// <summary>
        /// Python 2 <c>str</c> is a byte string. Decode as UTF-8 first (Ren'Py writes UTF-8) and
        /// fall back to Latin-1 so no byte sequence is ever lost.
        /// </summary>
        private static PickleValue MakePy2String(byte[] raw)
        {
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(raw);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Latin1.GetString(raw);
            }

            return PickleValue.MakeString(text, raw);
        }

        private static string DecodeUtf8(byte[] raw) => Encoding.UTF8.GetString(raw);

        private static byte[] DecodeQuotedString(byte[] line)
        {
            // Protocol 0 STRING: a Python repr such as 'abc\n' or "abc\x00".
            if (line.Length == 0)
            {
                return line;
            }

            var quote = line[0];

            if (quote is not ((byte)'\'' or (byte)'"'))
            {
                return line;
            }

            var end = line.Length;

            while (end > 1 && line[end - 1] != quote)
            {
                end--;
            }

            if (end <= 1)
            {
                end = line.Length;
            }
            else
            {
                end--; // drop the closing quote
            }

            var output = new List<byte>(end);

            for (var i = 1; i < end; i++)
            {
                var b = line[i];

                if (b != (byte)'\\' || i + 1 >= end)
                {
                    output.Add(b);
                    continue;
                }

                var e = line[++i];

                switch ((char)e)
                {
                    case 'n':
                        output.Add((byte)'\n');
                        break;
                    case 'r':
                        output.Add((byte)'\r');
                        break;
                    case 't':
                        output.Add((byte)'\t');
                        break;
                    case '\\':
                        output.Add((byte)'\\');
                        break;
                    case '\'':
                        output.Add((byte)'\'');
                        break;
                    case '"':
                        output.Add((byte)'"');
                        break;
                    case '0':
                        output.Add(0);
                        break;
                    case 'x':
                        if (i + 2 < end)
                        {
                            output.Add((byte)((HexValue(line[i + 1]) << 4) | HexValue(line[i + 2])));
                            i += 2;
                        }

                        break;
                    default:
                        output.Add(e);
                        break;
                }
            }

            return output.ToArray();
        }

        private static int HexValue(byte b) => b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => 0,
        };

        private static string DecodeRawUnicodeEscape(byte[] line)
        {
            var sb = new StringBuilder(line.Length);

            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] != (byte)'\\' || i + 1 >= line.Length)
                {
                    AppendUtf8Byte(sb, line, ref i);
                    continue;
                }

                var e = (char)line[++i];

                switch (e)
                {
                    case 'u' when i + 4 < line.Length:
                        sb.Append((char)ParseHex(line, i + 1, 4));
                        i += 4;
                        break;
                    case 'U' when i + 8 < line.Length:
                        sb.Append(char.ConvertFromUtf32((int)ParseHex(line, i + 1, 8)));
                        i += 8;
                        break;
                    case '\\':
                        sb.Append('\\');
                        break;
                    default:
                        sb.Append(e);
                        break;
                }
            }

            return sb.ToString();
        }

        private static void AppendUtf8Byte(StringBuilder sb, byte[] data, ref int index)
        {
            var start = index;
            var length = 1;

            if (data[start] >= 0xF0)
            {
                length = 4;
            }
            else if (data[start] >= 0xE0)
            {
                length = 3;
            }
            else if (data[start] >= 0xC0)
            {
                length = 2;
            }

            if (start + length > data.Length)
            {
                length = 1;
            }

            sb.Append(Encoding.UTF8.GetString(data, start, length));
            index = start + length - 1;
        }

        private static long ParseHex(byte[] data, int start, int count)
        {
            long value = 0;

            for (var i = 0; i < count; i++)
            {
                value = (value << 4) | (uint)HexValue(data[start + i]);
            }

            return value;
        }

        private static long ParseLong(string text)
        {
            text = text.Trim();

            if (text.Length == 0)
            {
                return 0;
            }

            return long.Parse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// Report-only threat classifier for globals referenced by a pickle stream. This is used to
/// *tell the caller* what a hostile save asked for; it is never consulted before resolving a
/// name, because the scanner never resolves names at all.
/// </summary>
internal static class PickleThreatClassifier
{
    private static readonly HashSet<string> DangerousModules = new(StringComparer.Ordinal)
    {
        "os", "posix", "nt", "subprocess", "commands", "popen2", "pty", "socket", "socketserver",
        "shutil", "sys", "runpy", "importlib", "imp", "ctypes", "multiprocessing", "asyncio",
        "signal", "resource", "fcntl", "pdb", "code", "codeop", "compileall", "pickle", "cPickle",
        "_pickle", "marshal", "shelve", "tempfile", "glob", "pathlib", "io", "fileinput",
        "webbrowser", "platform", "getpass", "urllib", "urllib2", "httplib", "http", "telnetlib",
        "ftplib", "smtplib", "xmlrpc", "requests", "builtins",
    };

    private static readonly HashSet<string> BenignBuiltinNames = new(StringComparer.Ordinal)
    {
        "dict", "list", "set", "frozenset", "tuple", "object", "str", "unicode", "bytes",
        "int", "float", "bool", "complex", "type", "bytearray", "range", "slice", "long",
    };

    internal static PickleThreatClassification Classify(string module, string name)
    {
        if (module is "copy_reg" or "copyreg")
        {
            return name is "_reconstructor" or "__newobj__" or "_extension_registry" or "_extension_cache"
                ? PickleThreatClassification.ReconstructorHelper
                : PickleThreatClassification.DangerousGlobal;
        }

        if (module is "__builtin__" or "builtins")
        {
            return BenignBuiltinNames.Contains(name)
                ? PickleThreatClassification.BenignBuiltin
                : PickleThreatClassification.DangerousGlobal;
        }

        if (module is "collections" or "_collections")
        {
            return PickleThreatClassification.BenignBuiltin;
        }

        if (module.StartsWith("renpy.", StringComparison.Ordinal)
            || module is "store" || module.StartsWith("store.", StringComparison.Ordinal)
            || module is "character" || module is "__main__")
        {
            return PickleThreatClassification.ApplicationClass;
        }

        return DangerousModules.Contains(module)
            ? PickleThreatClassification.DangerousGlobal
            : PickleThreatClassification.UnknownGlobal;
    }
}

/// <summary>Extension helpers kept separate so the scanner body stays readable.</summary>
internal static class PickleOpaqueExtensions
{
    /// <summary>Threat classification recorded for the construction that produced this placeholder.</summary>
    internal static PickleThreatClassification Classification(this PickleOpaque opaque) => opaque.Kind switch
    {
        PickleConstructionKind.Global => PickleThreatClassifier.Classify(opaque.ModuleName, opaque.TypeName),
        _ => opaque.Callable?.Opaque?.Classification() ?? PickleThreatClassification.ApplicationClass,
    };
}
