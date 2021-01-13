using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Disasm6502;

namespace emu2asm.NesMlb
{
    partial class Disassembler
    {
        private Config _config;
        private Rom _rom;
        private LabelDatabase _labelDb;
        private byte[] _coverage;
        private byte[] _tracedCoverage;
        private int[] _originCoverage;

        private readonly LabelNamespace _nullNamepsace = new();

        private List<Segment> _segments = new();
        private List<Segment> _fixedSegments = new();

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

            MakeSegments();

            _fixedSegments.Add( _config.Banks[1].Segments[1] );
            _fixedSegments.Add( _config.Banks[6].Segments[1] );
            _fixedSegments.Add( _config.Banks[7].Segments[0] );
            _fixedSegments.Add( _config.Banks[7].Segments[1] );
            _fixedSegments.Add( _config.Banks[7].Segments[2] );
        }

        private void MakeSegments()
        {
            foreach ( var bankInfo in _config.Banks )
            {
                string baseName = "BANK_" + bankInfo.Id;
                int i = 0;

                foreach ( var segment in bankInfo.Segments )
                {
                    if ( segment.Type == SegmentType.Program )
                    {
                        segment.Namespace = _labelDb.Program;
                        // Leave NamespaceBase = 0.
                    }
                    else if ( segment.Type == SegmentType.SaveRam )
                    {
                        segment.Namespace = _labelDb.SaveRam;
                        segment.NamespaceBase = segment.Offset + 0x6000 - segment.Address;
                    }

                    segment.Id = _segments.Count;

                    if ( string.IsNullOrEmpty( segment.Tag ) )
                        segment.Tag = i.ToString( "X2" );

                    segment.Name = string.Format( "BANK_{0}_{1}", bankInfo.Id, segment.Tag );

                    _segments.Add( segment );
                    i++;
                }
            }
        }

        public void Disassemble()
        {
            TraceLabels();
            ClearUnusedCoverageBit();
            MarkSaveRamCodeCoverage();
            GenerateSaveRamJumpLabels();
            TraceCode();

            foreach ( var bank in _config.Banks )
            {
                DisassembleBank( bank );
            }

            WriteDefinitionsFile();
            WriteLinkerScript();
        }

        private void DisassembleBank( Bank bankInfo )
        {
            var disasm = new Disasm6502.Disassembler();
            var dataBlock = new DataBlock();

            // TODO: delete all ASM files beforehand?

            string filename = string.Format( "Z_{0}.asm", bankInfo.Id );

            using var writer = new StreamWriter( filename, false, System.Text.Encoding.ASCII );

            writer.WriteLine( ".INCLUDE \"Variables.inc\"" );

            foreach ( var segment in bankInfo.Segments )
            {
                FlushDataBlock( dataBlock, writer );

                if ( segment.Type == SegmentType.SaveRam )
                    WriteRamSegmentLabel( segment, writer );

                writer.WriteLine();
                writer.WriteLine( ".SEGMENT \"{0}\"", segment.Name );
                writer.WriteLine();

                WriteImports( writer, segment );
                WriteExports( writer, segment );

                int endOffset = segment.Offset + segment.Size;

                for ( int romOffset = segment.Offset; romOffset < endOffset; romOffset++ )
                {
                    LabelRecord subjLabel;
                    object attribute = null;

                    ushort pc = (ushort) segment.GetAddress( romOffset );

                    int nsOffset = segment.GetNamespaceOffset( romOffset );

                    if ( segment.Namespace.ByAddress.TryGetValue( nsOffset, out subjLabel ) )
                    {
                        var commentParts = new CommentParts();

                        if ( !string.IsNullOrEmpty( subjLabel.Comment ) )
                        {
                            var commentParser = new CommentParser( subjLabel.Comment );
                            commentParts = commentParser.ParseAll();
                            attribute = commentParts.Attribute;
                        }

                        if ( !string.IsNullOrEmpty( subjLabel.Name ) )
                        {
                            FlushDataBlock( dataBlock, writer );

                            writer.WriteLine( "{0}:", subjLabel.Name );
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
                            if ( attribute is ExprCodeAttribute exprAttr )
                            {
                                memoryName = exprAttr.Expression;
                            }
                            else
                            {
                                var operand = FindAbsoluteAddressLabel( segment, inst.Value, romOffset );

                                if ( operand != null && !string.IsNullOrEmpty( operand.Name ) )
                                    memoryName = operand.Name;
                            }
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
                        if ( subjLabel != null && subjLabel.Length > 1
                            && (_tracedCoverage[romOffset] & 0x40) != 0 )
                        {
                            FlushDataBlock( dataBlock, writer );
                            WriteAddressTable( segment, romOffset, subjLabel, writer );
                            romOffset += subjLabel.Length - 1;
                        }
                        else if ( attribute != null )
                        {
                            FlushDataBlock( dataBlock, writer );

                            var dataAttr = attribute as DataAttribute;
                            if ( dataAttr == null )
                                throw new ApplicationException();

                            if ( !dataAttr.WriteBlock( this, segment, romOffset, subjLabel, writer ) )
                                WriteDataBlock( romOffset, subjLabel.Length, writer );

                            romOffset += subjLabel.Length - 1;
                        }
                        // TODO: make this optional
                        else if ( subjLabel != null && subjLabel.Length > 1 )
                        {
                            FlushDataBlock( dataBlock, writer );
                            WriteDataBlock( romOffset, subjLabel.Length, writer );
                            romOffset += subjLabel.Length - 1;
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
                }

                FlushDataBlock( dataBlock, writer );
            }
        }

        private void WriteRamSegmentLabel( Segment segment, StreamWriter writer )
        {
            if ( _labelDb.Program.ByAddress.TryGetValue( segment.Offset, out var record )
                && !string.IsNullOrEmpty( record.Name ) )
            {
                writer.WriteLine( "{0}:", record.Name );
            }
        }

        private void WriteAddressTable( Segment segment, int offset, LabelRecord label, StreamWriter writer )
        {
            byte[] image = _rom.Image;

            for ( int i = 0; i < label.Length; i += 2, offset += 2 )
            {
                ushort addr = (ushort) (image[offset] | (image[offset + 1] << 8));

                var operandLabel = FindAbsoluteAddressLabel( segment, addr, offset );
                string sOperand;

                if ( operandLabel != null && !string.IsNullOrEmpty( operandLabel.Name ) )
                    sOperand = operandLabel.Name;
                else
                    sOperand = string.Format( "${0:X4}", addr );

                writer.WriteLine( "    .WORD {0}", sOperand );
            }

            writer.WriteLine();
        }

        private static void WriteExports( StreamWriter writer, Segment segment )
        {
            var exports = new Export[segment.Exports.Count];

            segment.Exports.Values.CopyTo( exports, 0 );
            Array.Sort( exports, ( a, b ) => a.Label.Name.CompareTo( b.Label.Name ) );

            foreach ( var export in exports )
            {
                writer.WriteLine( ".EXPORT {0}", export.Label.Name );
            }

            writer.WriteLine();
        }

        private void WriteImports( StreamWriter writer, Segment segment )
        {
            List<Import>[] importsBySeg = new List<Import>[_segments.Count];

            foreach ( var import in segment.Imports.Values )
            {
                int sourceId = import.Source.Id;

                if ( _segments[sourceId].Type == SegmentType.Program )
                    sourceId = _segments[sourceId].Parent.Segments[0].Id;

                if ( importsBySeg[sourceId] == null )
                    importsBySeg[sourceId] = new List<Import>();

                importsBySeg[sourceId].Add( import );
            }

            foreach ( var importList in importsBySeg )
            {
                if ( importList != null && importList.Count > 0 )
                {
                    string sSource;

                    if ( importList[0].Source.Type == SegmentType.Program )
                        sSource = string.Format( "program bank {0}", importList[0].Source.Parent.Id );
                    else
                        sSource = string.Format( "RAM code bank {0}", importList[0].Source.Parent.Id );

                    writer.WriteLine( "\n; Imports from {0}\n", sSource );

                    var imports = importList.ToArray();
                    Array.Sort( imports, ( a, b ) => a.Label.Name.CompareTo( b.Label.Name ) );

                    foreach ( var import in imports )
                    {
                        writer.WriteLine( ".IMPORT {0}", import.Label.Name );
                    }
                }
            }

            writer.WriteLine();
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

        private LabelRecord FindAbsoluteAddressLabel( Segment segment, ushort addr, int instOffset )
        {
            LabelRecord record;
            LabelNamespace labelNamespace;
            int absOffset;

            if ( addr < 0x2000 )
            {
                absOffset = addr;
                labelNamespace = _labelDb.Ram;
            }
            else if ( addr < 0x6000 )
            {
                absOffset = addr;
                labelNamespace = _labelDb.Registers;
            }
            else if ( addr < 0x8000 )
            {
                absOffset = (addr - 0x6000);
                labelNamespace = _labelDb.SaveRam;
            }
            else if ( addr < 0xC000 )
            {
                if ( segment.Type == SegmentType.Program && segment.Parent.Address == 0x8000 )
                {
                    absOffset = (addr - 0x8000) + segment.Parent.Offset;
                    labelNamespace = _labelDb.Program;
                }
                else
                {
                    if ( (_tracedCoverage[instOffset] & TracedBankKnownFlag) != 0 )
                    {
                        int bank = _tracedCoverage[instOffset] & 0x0F;

                        absOffset = (addr - 0x8000) + _config.Banks[bank].Offset;
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
                absOffset = (addr - 0xC000) + 0x1C000;
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

        private static bool IsCallToSwitchableBank( InstDisasm inst )
        {
            return (inst.Class == Class.JMP || inst.Class == Class.JSR)
                && inst.Mode == Mode.a && inst.Value >= 0x8000 && inst.Value < 0xC000;
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

            CollateSwitchableExports();
            ReportTracedCalls();
        }

        private void TraceCallsInFixedBank()
        {
            TraceCallsInSegment( _config.Banks[7].Segments[0] );
            TraceCallsInSegment( _config.Banks[1].Segments[1] );
        }

        private void TraceCallsInSegment( Segment segment )
        {
            var disasm = new Disasm6502.Disassembler();

            var bankInfo = segment.Parent;

            Debug.Assert( segment.Address < 0x8000 || segment.Address >= 0xC000 );

            int  endOffset = bankInfo.Offset + bankInfo.Size;

            int  a = -1;
            int  mappedBank = -1;
            int  originOffset = -1;
            bool firstIter = true;
            var  branches = new Queue<BranchInfo>();

            branches.Enqueue( new BranchInfo( segment.Offset, -1, -1 ) );

            while ( branches.Count > 0 )
            {
                BranchInfo branchInfo = branches.Dequeue();
                mappedBank = branchInfo.MappedBank;
                originOffset = branchInfo.OriginOffset;

                if ( mappedBank == 4 )
                    Console.WriteLine( "Processing branch" );

                int offset = branchInfo.Offset;
                int addr = segment.Address + (offset - segment.Offset);

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
                            if ( inst.Value == 0xFFAC || inst.Value == 0xBFAC )
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
                                    if ( (record.Length % 2) != 0 )
                                        throw new ApplicationException();

                                    for ( int i = 0; i < record.Length; i += 2 )
                                    {
                                        int o = tableOffset + i;
                                        int addrEntry = _rom.Image[o] | (_rom.Image[o + 1] << 8);

                                        if ( segment.IsAddressInside( addrEntry ) )
                                        {
                                            Debug.Assert( segment.Type == SegmentType.Program );

                                            int branchOffset = addrEntry - bankInfo.Address + bankInfo.Offset;
                                            branches.Enqueue( new BranchInfo( branchOffset, mappedBank, originOffset ) );
                                        }

                                        _tracedCoverage[o + 0] = (byte) (mappedBank | TracedBankKnownFlag);
                                        _tracedCoverage[o + 1] = (byte) (mappedBank | TracedBankKnownFlag);
                                    }

                                    _tracedCoverage[tableOffset] |= 0x40;

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

                        if ( branch && mappedBank >= 0 && segment.IsAddressInside( inst.Value ) )
                        {
                            int branchOffset;

                            if ( segment.Type == SegmentType.Program )
                                branchOffset = inst.Value - bankInfo.Address + bankInfo.Offset;
                            else
                                branchOffset = inst.Value - segment.Address + segment.Offset;

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

        private void CollateSwitchableExports()
        {
            Segment curSegment;

            var disasm = new Disasm6502.Disassembler();

            foreach ( var bankInfo in _config.Banks )
            {
                foreach ( var segment in bankInfo.Segments )
                {
                    if ( segment.Address >= 0x8000 && segment.Address < 0xC000 )
                        continue;

                    int endOffset = segment.Offset + segment.Size;
                    int addr = segment.Address;

                    curSegment = segment;
                    ProcessBankCode( segment.Offset, endOffset, addr, ProcessInstruction );
                    ProcessSwitchableData( segment );
                }
            }

            void ProcessInstruction( InstDisasm inst, int offset )
            {
                if ( IsCallToSwitchableBank( inst ) )
                {
                    int bank = GetBankMapping( offset );

                    if ( bank >= 0 )
                    {
                        // TODO: for now, assume that prog banks are in the right order
                        var bankInfo = _config.Banks[bank];
                        int labelOffset = GetOffsetFromAddress( bankInfo, inst.Value );

                        if ( _labelDb.Program.ByAddress.TryGetValue( labelOffset, out LabelRecord record ) )
                        {
                            var sourceSeg = FindSegmentByAddress( bankInfo, inst.Value );
                            Debug.Assert( sourceSeg != null );

                            if ( sourceSeg.Parent != curSegment.Parent )
                            {
                                AddImport( curSegment, record, sourceSeg );
                                AddExport( sourceSeg, record );
                            }
                        }
                    }
                }
            }
        }

        void ProcessSwitchableData( Segment segment )
        {
            int endOffset = segment.Offset + segment.Size;
            byte[] image = _rom.Image;

            for ( int offset = segment.Offset; offset < endOffset; offset++ )
            {
                if ( (_coverage[offset] & 0x11) == 0 && (_tracedCoverage[offset] & 0x40) != 0 )
                {
                    int nsOffset = segment.GetNamespaceOffset( offset );
                    int bank = _tracedCoverage[offset] & 0x0F;
                    Bank bankInfo = _config.Banks[bank];

                    if ( segment.Namespace.ByAddress.TryGetValue( nsOffset, out var tableLabel )
                        && tableLabel.Length >= 2 )
                    {
                        for ( int i = 0; i < tableLabel.Length; i += 2, offset += 2 )
                        {
                            ushort addr = (ushort) (image[offset] | (image[offset + 1] << 8));

                            if ( addr >= 0x8000 && addr < 0xC000 )
                            {
                                var entryLabel = FindAbsoluteAddressLabel( segment, addr, offset );

                                if ( entryLabel != null && !string.IsNullOrEmpty( entryLabel.Name ) )
                                {
                                    Segment calleeSeg = FindSegmentByAddress( bankInfo, addr );

                                    AddImport( segment, entryLabel, calleeSeg );
                                    AddExport( calleeSeg, entryLabel );
                                }
                            }
                        }

                        offset--;
                    }
                }
            }
        }

        private void AddImport( Segment segment, LabelRecord record, Segment source )
        {
            if ( !segment.Imports.ContainsKey( record.Address ) )
            {
                var import = new Import( record, source );
                segment.Imports.Add( record.Address, import );
            }
        }

        private void AddExport( Segment segment, LabelRecord record )
        {
            if ( !segment.Exports.ContainsKey( record.Address ) )
            {
                var export = new Export( record );
                segment.Exports.Add( record.Address, export );
            }
        }

        private int GetBankMapping( int offset )
        {
            bool   mapped = (_tracedCoverage[offset] & TracedBankKnownFlag) != 0;
            int    bank = _tracedCoverage[offset] & 0x0F;

            return mapped ? bank : -1;
        }

        private int GetOffsetFromAddress( Bank bankInfo, int address )
        {
            return address - bankInfo.Address + bankInfo.Offset;
        }

        private int GetOffsetFromAddress( Segment segment, int address )
        {
            if ( segment.Type == SegmentType.Program )
                return address - segment.Parent.Address + segment.Parent.Offset;
            else
                return address - segment.Address + segment.Offset;
        }

        private Segment FindSegmentByAddress( Bank bankInfo, int address )
        {
            foreach ( var segment in bankInfo.Segments )
            {
                if ( segment.IsAddressInside( address ) )
                    return segment;
            }

            return null;
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
                if ( IsCallToSwitchableBank( inst ) )
                {
                    bool   traced = (_tracedCoverage[offset] & 0x40) != 0;
                    int    bank = GetBankMapping( offset );
                    bool   mapped = bank >= 0;
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
                        int labelOffset = GetOffsetFromAddress( _config.Banks[bank], inst.Value);

                        if ( _labelDb.Program.ByAddress.TryGetValue( labelOffset, out LabelRecord record ) )
                        {
                            memName = record.Name;
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

        private void TraceCallsToFixedBank()
        {
            int bankNumber = -1;
            Segment callerSeg;

            foreach ( var bankInfo in _config.Banks )
            {
                bankNumber++;

                foreach ( var segment in bankInfo.Segments )
                {
                    int endOffset = segment.Offset + segment.Size;
                    int addr = segment.Address;

                    callerSeg = segment;
                    ProcessBankCode( segment.Offset, endOffset, addr, ProcessInstruction );
                }
            }

            void ProcessInstruction( InstDisasm inst, int offset )
            {
                if ( !IsAbsolute( inst.Mode ) )
                    return;

                Segment calleeSeg = null;

                if ( inst.Class == Class.JSR && inst.Value == 0xE5E2
                    && callerSeg.Type == SegmentType.Program && callerSeg.Address < 0xC000 )
                {
                    int instLen = Disasm6502.Disassembler.GetInstructionLengthByMode( inst.Mode );

                    int tableOffset = offset + instLen;
                    LabelRecord tableLabel;

                    if ( _labelDb.Program.ByAddress.TryGetValue( tableOffset, out tableLabel ) )
                    {
                        // TODO: Validate the table length
                        if ( (tableLabel.Length % 2) != 0 )
                            throw new ApplicationException();

                        for ( int i = 0; i < tableLabel.Length; i += 2 )
                        {
                            int o = tableOffset + i;
                            ushort entryAddr = (ushort) (_rom.Image[o] | (_rom.Image[o + 1] << 8));

                            if ( !callerSeg.IsAddressInside( entryAddr ) )
                            {
                                calleeSeg = FindFixedSegmentByAddress( entryAddr );

                                if ( calleeSeg.Parent != callerSeg.Parent )
                                {
                                    int target = GetOffsetFromAddress( calleeSeg, entryAddr );

                                    int nsOffset = calleeSeg.GetNamespaceOffset( target );

                                    if ( calleeSeg.Namespace.ByAddress.TryGetValue( nsOffset, out var record ) )
                                    {
                                        AddImport( callerSeg, record, source: calleeSeg );
                                        AddExport( calleeSeg, record );
                                    }
                                }
                            }
                        }

                        _tracedCoverage[tableOffset] |= 0x40;
                    }
                }

                foreach ( var segment in _fixedSegments )
                {
                    if ( segment.IsAddressInside( inst.Value ) )
                    {
                        calleeSeg = segment;
                        break;
                    }
                }

                if ( calleeSeg == null || calleeSeg.Id == callerSeg.Id )
                    return;

                int targetOffset = GetOffsetFromAddress( calleeSeg, inst.Value );

                if ( (inst.Class == Class.JMP || inst.Class == Class.JSR)
                    && inst.Mode == Mode.a
                    && callerSeg.Type == SegmentType.Program && callerSeg.Address < 0xC000 )
                {
                    _tracedCoverage[targetOffset] = (byte) (bankNumber | 0x20);
                    _originCoverage[targetOffset] = offset;
                }

                if ( calleeSeg.Parent != callerSeg.Parent )
                {
                    int nsOffset = calleeSeg.GetNamespaceOffset( targetOffset );

                    if ( calleeSeg.Namespace.ByAddress.TryGetValue( nsOffset, out LabelRecord record ) )
                    {
                        AddImport( callerSeg, record, source: calleeSeg );
                        AddExport( calleeSeg, record );
                    }
                }
            }
        }

        private Segment FindFixedSegmentByAddress( int address )
        {
            foreach ( var segment in _fixedSegments )
            {
                if ( segment.IsAddressInside( address ) )
                    return segment;
            }

            return null;
        }

        private void MarkSaveRamCodeCoverage()
        {
            // Mark the code bytes that are copied to Save RAM.

            foreach ( var segment in _segments )
            {
                if ( segment.Type != SegmentType.SaveRam )
                    continue;

                int startOffset = segment.Offset;
                int endOffset = segment.Offset + segment.Size;
                int endRamAddr = segment.Address + segment.Size;

                Array.Fill<byte>( _coverage, 0x10, startOffset, segment.Size );

                foreach ( var record in _labelDb.SaveRam.ByName.Values )
                {
                    int address = record.Address + 0x6000;

                    if ( address < segment.Address
                        || address >= endRamAddr
                        || record.Length <= 1 )
                        continue;

                    int offset = (address - segment.Address) + startOffset;

                    Array.Fill<byte>( _coverage, 0x02, offset, record.Length );
                }
            }
        }

        private void GenerateSaveRamJumpLabels()
        {
            // Generate labels for jumps in code copied to Save RAM.

            foreach ( var segment in _segments )
            {
                if ( segment.Type != SegmentType.SaveRam )
                    continue;

                int startOffset = segment.Offset;
                int endOffset = startOffset + segment.Size;
                int addr = segment.Address;

                if ( segment.MemoryUse == MemoryUse.Data )
                    Array.Fill<byte>( _coverage, 0x02, startOffset, segment.Size );
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
                        _labelDb.SaveRam.ByName.Add( record.Name, record );
                    }
                    else if ( string.IsNullOrEmpty( record.Name ) )
                    {
                        record.Name = labelName;
                        _labelDb.SaveRam.ByName.Add( record.Name, record );
                    }
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

        private void TraceLabels()
        {
            foreach ( var bankInfo in _config.Banks )
            {
                foreach ( var segment in bankInfo.Segments )
                {
                    int endOffset = segment.Offset + segment.Size;
                    int nsOffset = segment.GetNamespaceOffset( segment.Offset );

                    for ( int offset = segment.Offset; offset < endOffset; )
                    {
                        int size = 1;

                        if ( segment.Namespace.ByAddress.TryGetValue( nsOffset, out var label )
                            && !string.IsNullOrEmpty( label.Comment ) )
                        {
                            var parser = new CommentParser( label.Comment );
                            var attribute = parser.ParseAttribute();

                            if ( attribute != null )
                            {
                                var dataAttr = attribute as DataAttribute;

                                if ( dataAttr != null )
                                {
                                    if ( (_coverage[offset] & 0x11) != 0 )
                                        throw new Exception( "Data attribute was applied to code." );

                                    dataAttr.ProcessBlock( this, segment, offset, label );
                                    size = label.Length;
                                }
                            }
                        }

                        offset += size;
                        nsOffset += size;
                    }
                }
            }
        }

        private void WriteDefinitionsFile()
        {
            using var writer = new StreamWriter( "Variables.inc", false, System.Text.Encoding.ASCII );

            foreach ( var pair in _labelDb.Ram.ByName )
            {
                int address = pair.Value.Address;

                writer.WriteLine( "{0} := ${1:X2}", pair.Key, address );
            }

            writer.WriteLine();

            foreach ( var pair in _labelDb.Registers.ByName )
            {
                int address = pair.Value.Address;

                writer.WriteLine( "{0} := ${1:X2}", pair.Key, address );
            }

            writer.WriteLine();

            foreach ( var pair in _labelDb.SaveRam.ByName )
            {
                // TODO:

                int address = pair.Value.Address + 0x6000;

                // TODO: hardcoded addresses

                if ( address < 0x67F0 || address >= 0x7F00
                    || (address >= 0x687E && address < 0x6C90) )
                {
                    writer.WriteLine( "{0} := ${1:X4}", pair.Key, address );
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
                    "    ROM_{0}: start = ${1:X4}, size = ${2:X4}, file = \"bank_{0}.bin\", fill = yes, fillval = $FF ;",
                    bankInfo.Id,
                    bankInfo.Address,
                    bankInfo.Size );

                foreach ( var segment in bankInfo.Segments )
                {
                    if ( segment.Type == SegmentType.SaveRam )
                    {
                        writer.WriteLine(
                            "    RAM_{0}_{3}: start = ${1:X4}, size = ${2:X4}, file = \"\", fill = yes, fillval = $FF ;",
                            bankInfo.Id,
                            segment.Address,
                            segment.Size,
                            segment.Tag );
                    }
                }
            }
            writer.WriteLine( "}\n" );

            writer.WriteLine( "SEGMENTS\n{" );
            foreach ( var segment in _segments )
            {
                if ( segment.Type == SegmentType.Program )
                {
                    writer.Write(
                        "    {0}: load = ROM_{1}, type = ro",
                        segment.Name,
                        segment.Parent.Id );
                    if ( segment.Address != segment.Parent.Address )
                        writer.Write( ", start = ${0:X4}", segment.Address );
                    writer.WriteLine( " ;" );
                }
                else if ( segment.Type == SegmentType.SaveRam )
                {
                    writer.WriteLine(
                        "    {0}: load = ROM_{1}, type = ro, run = RAM_{1}_{2}, define = yes ;",
                        segment.Name,
                        segment.Parent.Id,
                        segment.Tag );
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
