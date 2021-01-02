#define GLOBALS

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Disasm6502;

namespace emu2asm.NesMlb
{
    class Disassembler
    {
        private Config _config;
        private Rom _rom;
        private LabelDatabase _labelDb;
        private byte[] _coverage;
        private byte[] _tracedCoverage;
        private int[] _originCoverage;

        private List<LabelRecord> _importsForFixedBank = new();
        private List<string>[] _exportsByBank;

        private Dictionary<int, LabelRecord>[] _importsByBank;
        private string[] _exportsForFixedBank;

        private readonly LabelNamespace _nullNamepsace = new();

        public Disassembler(
            Config config,
            Rom rom,
            byte[] coverageImage,
            LabelDatabase labelDatabase )
        {
            _config = config;
            _rom = rom;
            _coverage = coverageImage;
            _labelDb = labelDatabase;

            _tracedCoverage = new byte[coverageImage.Length];
            _originCoverage = new int[coverageImage.Length];

            _importsByBank = new Dictionary<int, LabelRecord>[_config.Banks.Count];

            for ( int i = 0; i < _config.Banks.Count; i++ )
                _importsByBank[i] = new Dictionary<int, LabelRecord>();
        }

        public void Disassemble()
        {
            ClearUnusedCoverageBit();
            TraceCode();
            MarkSaveRamCodeCoverage();
            GenerateSaveRamJumpLabels();

            // TODO: Only include definitions that are used in each bank.

            StringBuilder builder = new StringBuilder();

            foreach ( var pair in _labelDb.Ram.ByName )
            {
                int address = pair.Value.Address;

                builder.AppendFormat( "{0} := ${1:X2}\n", pair.Key, address );
            }

            foreach ( var pair in _labelDb.Registers.ByName )
            {
                int address = pair.Value.Address;

                builder.AppendFormat( "{0} := ${1:X2}\n", pair.Key, address );
            }

            builder.AppendLine();

            foreach ( var pair in _labelDb.SaveRam.ByName )
            {
                // TODO:

                int address = pair.Value.Address + 0x6000;

                // TODO: hardcoded addresses

                if ( address < 0x67F0 || address >= 0x7F00
                    || (address >= 0x687E && address < 0x6C90) )
                {
                    builder.AppendFormat( "{0} := ${1:X4}\n", pair.Key, address );
                }
                else
                {
                    // TODO: IMPORT as needed

                    builder.AppendFormat( ".GLOBAL {0}\n", pair.Key );
                }
            }

            builder.AppendLine();

#if GLOBALS
            foreach ( var pair in _labelDb.Program.ByName )
            {
                // TODO:

                if ( pair.Value.Address < 0x1C000 )
                    continue;

                // TODO: IMPORT as needed

                builder.AppendFormat( ".GLOBAL {0}\n", pair.Key );
            }

            builder.AppendLine();
#endif

            string definitions = builder.ToString();

            foreach ( var bank in _config.Banks )
            {
                DisassembleBank( bank, definitions );
            }

            WriteLinkerScript();
        }

        private void DisassembleBank( Bank bankInfo, string definitions )
        {
            var disasm = new Disasm6502.Disassembler();
            var dataBlock = new DataBlock();
            var segment = FindSegment( bankInfo.Offset, bankInfo );

            // TODO: delete all ASM files beforehand?

            string filename = string.Format( "Z_{0}.asm", bankInfo.Id );

            using var writer = new StreamWriter( filename, false, System.Text.Encoding.ASCII );

            writer.WriteLine();
            writer.WriteLine( ".SEGMENT \"{0}\"", segment.Name );
            writer.WriteLine();
            writer.WriteLine( definitions );

#if !GLOBALS
            if ( bankInfo.Address == 0xC000 )
            {
                foreach ( var name in _exportsForFixedBank )
                {
                    writer.WriteLine( ".EXPORT {0}", name );
                }
            }
            else
            {
                int bankNumber = int.Parse( bankInfo.Id );

                foreach ( var pair in _importsByBank[bankNumber] )
                {
                    writer.WriteLine( ".IMPORT {0}", pair.Value.Name );
                }
            }
#endif

            if ( bankInfo.Address == 0xC000 )
            {
                foreach ( var record in _importsForFixedBank )
                {
                    writer.WriteLine( ".IMPORT {0}", record.Name );
                }
            }
            else
            {
                int bankNumber = int.Parse( bankInfo.Id );

                foreach ( var name in _exportsByBank[bankNumber] )
                {
                    writer.WriteLine( ".EXPORT {0}", name );
                }
            }

            int endOffset = bankInfo.Offset + bankInfo.Size;

            for ( int romOffset = bankInfo.Offset; romOffset < endOffset; romOffset++ )
            {
                if ( !segment.IsOffsetInside( romOffset ) )
                {
                    FlushDataBlock( dataBlock, writer );

                    segment = FindSegment( romOffset, bankInfo );

                    writer.WriteLine( "\n.SEGMENT \"{0}\"\n", segment.Name );
                }

                ushort pc = (ushort) segment.GetAddress( romOffset );

                int nsOffset = segment.GetNamespaceOffset( romOffset );

                if ( segment.Namespace.ByAddress.TryGetValue( nsOffset, out var record ) )
                {
                    if ( !string.IsNullOrEmpty( record.Name ) )
                    {
                        FlushDataBlock( dataBlock, writer );

                        writer.WriteLine( "{0}:", record.Name );
                    }

                    // TODO: comments
                }

                byte c = _coverage[romOffset];

                if ( (c & 0x11) != 0 )
                {
                    FlushDataBlock( dataBlock, writer );

                    disasm.PC = pc;
                    InstDisasm inst = disasm.Disassemble( _rom.Image, romOffset );
                    string memoryName = null;

                    int  instLen = Disasm6502.Disassembler.GetInstructionLengthByMode( inst.Mode );
                    if ( instLen < 1 )
                        ThrowBadInstructionError( romOffset );

                    if ( IsZeroPage( inst.Mode ) || inst.Mode == Mode.r
                        || (IsAbsolute( inst.Mode ) && !WritesToRom( inst )) )
                    {
                        record = FindAbsoluteAddress( bankInfo, inst, romOffset );

                        if ( record != null && !string.IsNullOrEmpty( record.Name ) )
                            memoryName = record.Name;
                    }

                    writer.Write( "    " );
                    disasm.Format( inst, memoryName, writer );
                    writer.WriteLine();

                    if ( inst.Class == Class.JMP
                        || inst.Class == Class.RTS
                        || inst.Class == Class.RTI )
                        writer.WriteLine();

                    romOffset += instLen - 1;
                }
                else
                {
                    if ( dataBlock.Known != ((c & 0x22) != 0) )
                    {
                        FlushDataBlock( dataBlock, writer );
                    }

                    if ( dataBlock.Size == 0 )
                    {
                        dataBlock.Offset = romOffset;
                        dataBlock.Known = (c & 0x22) != 0;
                    }

                    dataBlock.Size++;
                }
            }

            FlushDataBlock( dataBlock, writer );
        }

        private Segment FindSegment( int offset, Bank bankInfo )
        {
            string name = "BANK_" + bankInfo.Id;

            if ( bankInfo.RomToRam != null )
            {
                var romToRam = bankInfo.RomToRam;
                int segmentOffset = (romToRam.RomAddress - bankInfo.Address) + bankInfo.Offset;
                int endOffset = segmentOffset + romToRam.Size;

                if ( offset >= segmentOffset && offset < endOffset )
                    return new Segment(
                        segmentOffset,
                        romToRam.RamAddress,
                        romToRam.Size,
                        _labelDb.SaveRam,
                        segmentOffset + 0x6000 - romToRam.RamAddress,
                        name + "_RAM" );

                if ( offset < endOffset )
                    return new Segment(
                        bankInfo.Offset,
                        bankInfo.Address,
                        segmentOffset - bankInfo.Offset,
                        _labelDb.Program, 0, name );

                name += "_CONT";
            }

            return new Segment(
                bankInfo.Offset,
                bankInfo.Address,
                bankInfo.Size,
                _labelDb.Program, 0, name );
        }

        private record Segment
        {
            public int Offset;
            public int Address;
            public int Size;
            public LabelNamespace Namespace;
            public int NamespaceBase;
            public string Name;

            public Segment(
                int offset, int address, int size,
                LabelNamespace labelNamespace, int nsBase, string name )
            {
                Offset = offset;
                Address = address;
                Size = size;
                Namespace = labelNamespace;
                NamespaceBase = nsBase;
                Name = name;
            }

            public bool IsOffsetInside( int offset )
            {
                int endOffset = Offset + Size;
                return offset >= Offset && offset < endOffset;
            }

            public int GetAddress( int offset )
            {
                return (offset - Offset) + Address;
            }

            public int GetNamespaceOffset( int offset )
            {
                return offset - NamespaceBase;
            }
        }

        private record DataBlock
        {
            public int Offset;
            public int Size;
            public bool Known;
        }

        private void FlushDataBlock( DataBlock block, StreamWriter writer )
        {
            if ( block.Size > 0 )
            {
                if ( !block.Known )
                    writer.WriteLine( "; Unknown block" );

                WriteDataBlock( block.Offset, block.Size, writer );
                block.Size = 0;
            }
        }

        private LabelRecord FindAbsoluteAddress( Bank bankInfo, InstDisasm inst, int instOffset )
        {
            LabelRecord record;
            LabelNamespace labelNamespace;
            int absOffset;

            if ( inst.Value < 0x2000 )
            {
                absOffset = inst.Value;
                labelNamespace = _labelDb.Ram;
            }
            else if ( inst.Value < 0x6000 )
            {
                absOffset = inst.Value;
                labelNamespace = _labelDb.Registers;
            }
            else if ( inst.Value < 0x8000 )
            {
                absOffset = (inst.Value - 0x6000);
                labelNamespace = _labelDb.SaveRam;
            }
            else if ( inst.Value < 0xC000 )
            {
                if ( bankInfo.Address == 0x8000 )
                {
                    absOffset = (inst.Value - 0x8000) + bankInfo.Offset;
                    labelNamespace = _labelDb.Program;
                }
                else
                {
                    if ( (_tracedCoverage[instOffset] & TracedBankKnownFlag) != 0 )
                    {
                        int bank = _tracedCoverage[instOffset] & 0x0F;

                        absOffset = (inst.Value - 0x8000) + _config.Banks[bank].Offset;
                        labelNamespace = _labelDb.Program;
                    }
                    else
                    {
                        absOffset = 0;
                        labelNamespace = _nullNamepsace;
                    }
                }
            }
            else
            {
                absOffset = (inst.Value - 0xC000) + 0x1C000;
                labelNamespace = _labelDb.Program;
            }

            if ( labelNamespace.ByAddress.TryGetValue( absOffset, out record ) )
                return record;

            return null;
        }

        private void WriteDataBlock( int start, int length, StreamWriter writer )
        {
            while ( length > 0 )
            {
                writer.Write( "    .BYTE " );

                int lengthToWrite = (length > 8) ? 8 : length;

                for ( int i = 0; i < lengthToWrite; i++ )
                {
                    if ( i > 0 )
                        writer.Write( ", " );

                    writer.Write( "${0:X2}", _rom.Image[start] );
                    start++;
                }

                length -= lengthToWrite;

                writer.WriteLine();
            }

            writer.WriteLine();
        }

        private static bool IsAbsolute( Mode mode )
        {
            switch ( mode )
            {
                case Mode.a:
                case Mode.aN:
                case Mode.ax:
                case Mode.ay:
                    return true;
            }

            return false;
        }

        private static bool IsZeroPage( Mode mode )
        {
            switch ( mode )
            {
                case Mode.zp:
                case Mode.zpNy:
                case Mode.zpx:
                case Mode.zpxN:
                case Mode.zpy:
                    return true;
            }

            return false;
        }

        private static bool WritesToRom( InstDisasm inst )
        {
            if ( inst.Class != Class.STA
                && inst.Class != Class.STX
                && inst.Class != Class.STY )
                return false;

            return inst.Value >= 0x8000;
        }

        private const byte TracedBankKnownFlag = 0x80;

        private void ClearUnusedCoverageBit()
        {
            // Make sure unused bit 7 is truly unused.

            for ( int i = 0; i < _coverage.Length; i++ )
            {
                _coverage[i] &= 0x7F;
            }
        }

        private struct BranchInfo
        {
            public int Offset;
            public int MappedBank;
            public int OriginOffset;

            public BranchInfo( int offset, int mappedBank, int originOffset )
            {
                Offset = offset;
                MappedBank = mappedBank;
                OriginOffset = originOffset;
            }
        }

        private void TraceCode()
        {
            TraceCallsToFixedBank();
            TraceCallsInFixedBank();
            ReportTracedCalls();
            CollateFixedImports();
            CollateFixedExports();
        }

        private void TraceCallsInFixedBank()
        {
            var disasm = new Disasm6502.Disassembler();

            foreach ( var bankInfo in _config.Banks )
            {
                if ( bankInfo.Address != 0xC000 )
                    continue;

                int  endOffset = bankInfo.Offset + bankInfo.Size;

                int  a = -1;
                int  mappedBank = -1;
                int  originOffset = -1;
                bool firstIter = true;
                var  branches = new Queue<BranchInfo>();

                branches.Enqueue( new BranchInfo( bankInfo.Offset, -1, -1 ) );

                while ( branches.Count > 0 )
                {
                    BranchInfo branchInfo = branches.Dequeue();
                    mappedBank = branchInfo.MappedBank;
                    originOffset = branchInfo.OriginOffset;

                    if ( mappedBank == 4 )
                        Console.WriteLine( "Processing branch" );

                    int offset = branchInfo.Offset;
                    int addr = bankInfo.Address + (offset - bankInfo.Offset);

                    while ( offset < endOffset )
                    {
                        byte c = _coverage[offset];

                        if ( (c & 0x11) != 0 )
                        {
                            if ( (_tracedCoverage[offset] & TracedBankKnownFlag) != 0 )
                                break;

                            if ( (_tracedCoverage[offset] & 0x20) != 0 )
                            {
                                mappedBank = _tracedCoverage[offset] & 0x0F;
                                originOffset = _originCoverage[offset];
                                _tracedCoverage[offset] = (byte) (_tracedCoverage[offset] & ~0x20);

                                if ( mappedBank == 4 )
                                    Console.WriteLine( "Traced from bank 5" );
                            }

                            if ( !firstIter )
                                _tracedCoverage[offset] |= 0x40;

                            disasm.PC = (ushort) addr;
                            InstDisasm inst = disasm.Disassemble( _rom.Image, offset );

                            int  instLen = Disasm6502.Disassembler.GetInstructionLengthByMode( inst.Mode );
                            bool branch = false;
                            bool breakAfterBranch = false;
                            bool invalidateBankAfterBranch = false;

                            if ( instLen < 1 )
                                ThrowBadInstructionError( offset );

                            if ( mappedBank >= 0 )
                            {
                                _tracedCoverage[offset] = (byte) (mappedBank | TracedBankKnownFlag);
                                _originCoverage[offset] = originOffset;

                                if ( mappedBank == 4 )
                                    Console.WriteLine( "{0:X5}", offset );
                            }

                            if ( inst.Class == Class.LDA )
                            {
                                if ( inst.Mode == Mode.I )
                                    a = inst.Value;
                                else
                                    a = -1;
                            }
                            else if ( inst.Class == Class.JSR )
                            {
                                if ( inst.Value == 0xFFAC )
                                {
                                    if ( a < 0 )
                                        throw new ApplicationException( "Call to switch bank, but A is not set." );

                                    mappedBank = a;
                                    originOffset = offset;
                                }
                                else if ( inst.Value == 0xE5E2 )
                                {
                                    int tableOffset = offset + instLen;
                                    LabelRecord record;

                                    if ( mappedBank >= 0
                                        && _labelDb.Program.ByAddress.TryGetValue( tableOffset, out record ) )
                                    {
                                        // TODO: Validate the table length

                                        for ( int i = 0; i < record.Length; i += 2 )
                                        {
                                            int o = tableOffset + i;
                                            int addrEntry = _rom.Image[o] | (_rom.Image[o + 1] << 8);

                                            if ( addrEntry >= 0xC000 )
                                            {
                                                int branchOffset = addrEntry - bankInfo.Address + bankInfo.Offset;
                                                branches.Enqueue( new BranchInfo( branchOffset, mappedBank, originOffset ) );
                                            }
                                        }

                                        if ( !firstIter )
                                            break;
                                    }
                                }
                                else
                                {
                                    branch = true;
                                }
                            }
                            else if ( inst.Class == Class.RTS || inst.Class == Class.RTI )
                            {
                                if ( !firstIter )
                                    break;
                            }
                            else if ( inst.Class == Class.JMP )
                            {
                                if ( inst.Mode == Mode.a )
                                    branch = true;

                                invalidateBankAfterBranch = true;

                                if ( !firstIter )
                                    breakAfterBranch = true;
                            }
                            else if ( inst.Mode == Mode.r )
                            {
                                branch = true;
                            }

                            if ( branch && inst.Value >= 0xC000 && mappedBank >= 0 )
                            {
                                int branchOffset = inst.Value - bankInfo.Address + bankInfo.Offset;
                                branches.Enqueue( new BranchInfo( branchOffset, mappedBank, originOffset ) );
                            }

                            if ( invalidateBankAfterBranch )
                            {
                                mappedBank = -1;
                                originOffset = -1;
                            }

                            if ( breakAfterBranch )
                                break;

                            offset += instLen;
                            addr += instLen;
                        }
                        else
                        {
                            if ( !firstIter )
                                break;

                            mappedBank = -1;
                            originOffset = -1;

                            offset++;
                            addr++;
                        }
                    }

                    firstIter = false;
                }
            }
        }

        private void ReportTracedCalls()
        {
            var disasm = new Disasm6502.Disassembler();

            foreach ( var bankInfo in _config.Banks )
            {
                if ( bankInfo.Address != 0xC000 )
                    continue;

                int endOffset = bankInfo.Offset + bankInfo.Size;
                int addr = bankInfo.Address;

                ProcessBankCode( bankInfo.Offset, endOffset, addr, ProcessInstruction );
            }

            void ProcessInstruction( InstDisasm inst, int offset )
            {
                if ( (inst.Class == Class.JMP || inst.Class == Class.JSR)
                    && IsAbsolute( inst.Mode ) && inst.Value >= 0x8000 && inst.Value < 0xC000 )
                {
                    bool   traced = (_tracedCoverage[offset] & 0x40) != 0;
                    bool   mapped = (_tracedCoverage[offset] & TracedBankKnownFlag) != 0;
                    int    bank = _tracedCoverage[offset] & 0x0F;
                    string memName = null;
                    bool   labelNotFound = false;

                    Console.Write( traced ? "T " : "  " );

                    if ( mapped )
                        Console.Write( "{0:X1} ", bank );
                    else
                        Console.Write( "  " );

                    if ( mapped )
                    {
                        // TODO: for now, assume that prog banks are in the right order
                        int labelOffset = inst.Value - 0x8000 + _config.Banks[bank].Offset;

                        if ( _labelDb.Program.ByAddress.TryGetValue( labelOffset, out LabelRecord record ) )
                        {
                            memName = record.Name;
                            _importsForFixedBank.Add( record );
                        }
                        else
                        {
                            labelNotFound = true;
                        }
                    }

                    Console.Write( "{0:X5}: ", offset );
                    disasm.Format( inst, memName, Console.Out );
                    if ( mapped )
                        Console.Write( " \t({0:X5})", _originCoverage[offset] );
                    Console.WriteLine();

                    if ( labelNotFound )
                    {
                        Console.WriteLine(
                            "  Bank {0} was found was mapped, but a label wasn't found for address {1:X4}.",
                            bank, inst.Value );
                    }
                }
            }
        }

        private void CollateFixedImports()
        {
            _exportsByBank = new List<string>[_config.Banks.Count];

            for ( int i = 0; i < _config.Banks.Count; i++ )
                _exportsByBank[i] = new List<string>();

            foreach ( var record in _importsForFixedBank )
            {
                if ( record.Type != LabelType.Program )
                    continue;

                // We know that every bank is 0x4000 bytes.

                int bank = (int) ((uint) record.Address >> 14);

                _exportsByBank[bank].Add( record.Name );
            }
        }

        private void CollateFixedExports()
        {
            var records = new HashSet<LabelRecord>();

            foreach ( var importMap in _importsByBank )
            {
                foreach ( var record in importMap.Values )
                    records.Add( record );
            }

            _exportsForFixedBank = new string[records.Count];
            int i = 0;

            foreach ( var record in records )
            {
                _exportsForFixedBank[i++] = record.Name;
            }
        }

        private void TraceCallsToFixedBank()
        {
            int bankNumber = -1;

            foreach ( var bankInfo in _config.Banks )
            {
                bankNumber++;

                if ( bankInfo.Address == 0xC000 )
                    continue;

                int endOffset = bankInfo.Offset + bankInfo.Size;
                int addr = bankInfo.Address;

                ProcessBankCode( bankInfo.Offset, endOffset, addr, ProcessInstruction );
            }

            void ProcessInstruction( InstDisasm inst, int offset )
            {
                int targetOffset = inst.Value - 0xC000 + 0x1C000;

                if ( (inst.Class == Class.JMP || inst.Class == Class.JSR)
                    && inst.Mode == Mode.a
                    && inst.Value >= 0xC000 )
                {
                    _tracedCoverage[targetOffset] = (byte) (bankNumber | 0x20);
                    _originCoverage[targetOffset] = offset;
                }

                if ( IsAbsolute( inst.Mode ) && inst.Value >= 0xC000 )
                {
                    if ( !_importsByBank[bankNumber].TryGetValue( targetOffset, out LabelRecord record ) )
                    {
                        if ( _labelDb.Program.ByAddress.TryGetValue( targetOffset, out record ) )
                            _importsByBank[bankNumber].Add( targetOffset, record );
                    }
                }
            }
        }

        private void MarkSaveRamCodeCoverage()
        {
            // Mark the code bytes that are copied to Save RAM.

            foreach ( var bankInfo in _config.Banks )
            {
                if ( bankInfo.RomToRam == null )
                    continue;

                var romToRam = bankInfo.RomToRam;
                int startOffset = (bankInfo.RomToRam.RomAddress - bankInfo.Address) + bankInfo.Offset;
                int endOffset = startOffset + bankInfo.RomToRam.Size;
                int endRamAddr = romToRam.RamAddress + romToRam.Size;

                Array.Fill<byte>( _coverage, 0x10, startOffset, bankInfo.RomToRam.Size );

                foreach ( var record in _labelDb.SaveRam.ByName.Values )
                {
                    int address = record.Address + 0x6000;

                    if ( address < romToRam.RamAddress
                        || address >= endRamAddr
                        || record.Length <= 1 )
                        continue;

                    int offset = (address - romToRam.RamAddress) + startOffset;

                    Array.Fill<byte>( _coverage, 0x02, offset, record.Length );
                }
            }
        }

        private void GenerateSaveRamJumpLabels()
        {
            // Generate labels for jumps in code copied to Save RAM.

            foreach ( var bankInfo in _config.Banks )
            {
                if ( bankInfo.RomToRam == null )
                    continue;

                var romToRam = bankInfo.RomToRam;
                int startOffset = (bankInfo.RomToRam.RomAddress - bankInfo.Address) + bankInfo.Offset;
                int endOffset = startOffset + bankInfo.RomToRam.Size;
                int addr = romToRam.RamAddress;

                if ( bankInfo.RomToRam.Type == MemoryUse.Data )
                    Array.Fill<byte>( _coverage, 0x02, startOffset, romToRam.Size );
                else
                    ProcessBankCode( startOffset, endOffset, addr, ProcessInstruction );
            }

            void ProcessInstruction( InstDisasm inst, int offset )
            {
                if ( inst.Mode != Mode.r )
                    return;

                int relAddr = inst.Value - 0x6000;
                string labelName = string.Format( "L{0:X4}", inst.Value );

                // If found, then it has a comment but no name.

                if ( !_labelDb.SaveRam.ByName.TryGetValue( labelName, out LabelRecord record ) )
                {
                    if ( !_labelDb.SaveRam.ByAddress.TryGetValue( relAddr, out record ) )
                    {
                        record = new LabelRecord
                        {
                            Type = LabelType.SaveRam,
                            Address = relAddr,
                            Name = labelName,
                            Length = 1
                        };

                        _labelDb.SaveRam.ByAddress.Add( relAddr, record );
                    }
                    else
                    {
                        record.Name = labelName;
                    }

                    _labelDb.SaveRam.ByName.Add( record.Name, record );
                }
            }
        }

        private delegate void ProcessInstructionDelegate( InstDisasm inst, int offset );

        private void ProcessBankCode(
            int startOffset, int endOffset, int addr,
            ProcessInstructionDelegate processInstruction )
        {
            var disasm = new Disasm6502.Disassembler();

            for ( int offset = startOffset; offset < endOffset; )
            {
                byte c = _coverage[offset];

                if ( (c & 0x11) != 0 )
                {
                    disasm.PC = (ushort) addr;
                    InstDisasm inst = disasm.Disassemble( _rom.Image, offset );

                    int  instLen = Disasm6502.Disassembler.GetInstructionLengthByMode( inst.Mode );
                    if ( instLen < 1 )
                        ThrowBadInstructionError( offset );

                    processInstruction( inst, offset );

                    offset += instLen;
                    addr += instLen;
                }
                else
                {
                    offset++;
                    addr++;
                }
            }
        }

        private void WriteLinkerScript()
        {
            using var writer = new StreamWriter( "nes.cfg", false, System.Text.Encoding.ASCII );

            writer.WriteLine( "MEMORY\n{" );
            foreach ( var bankInfo in _config.Banks )
            {
                writer.WriteLine(
                    "    MEM_{0}: start = ${1:X4}, size = ${2:X4}, file = \"bank_{0}.bin\", fill = yes, fillval = $00 ;",
                    bankInfo.Id,
                    bankInfo.Address,
                    bankInfo.Size );

                if ( bankInfo.RomToRam != null )
                {
                    writer.WriteLine(
                        "    MEM_{0}_RAM: start = ${1:X4}, size = ${2:X4}, file = \"\", fill = yes, fillval = $00 ;",
                        bankInfo.Id,
                        bankInfo.RomToRam.RamAddress,
                        bankInfo.RomToRam.Size );
                }
            }
            writer.WriteLine( "}\n" );

            writer.WriteLine( "SEGMENTS\n{" );
            foreach ( var bankInfo in _config.Banks )
            {
                writer.WriteLine(
                    "    BANK_{0}: load = MEM_{0}, type = ro, align = $4000 ;",
                    bankInfo.Id );

                if ( bankInfo.RomToRam != null )
                {
                    writer.WriteLine(
                        "    BANK_{0}_RAM: load = MEM_{0}, type = ro, run = MEM_{0}_RAM, define = yes ;",
                        bankInfo.Id );

                    writer.WriteLine(
                        "    BANK_{0}_CONT: load = MEM_{0}, type = ro ;",
                        bankInfo.Id );
                }
            }
            writer.WriteLine( "}" );
        }

        private static void ThrowBadInstructionError( int offset )
        {
            string message = string.Format(
                "Found a bad instruction at program offset {0:X5}",
                offset );
            throw new ApplicationException( message );
        }
    }
}
