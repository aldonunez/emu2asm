using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

        private readonly LabelNamespace _nullNamepsace = new( LabelType.Ram );

        private List<Segment> _segments = new();
        private List<Segment> _fixedSegments = new();

        private Regex _procPatternRegex;
        private StringBuilder _unnamedLabelStringBuilder = new();
        private BlockLabelComparer _blockLabelComparer = new();

        public bool SeparateUnknown { get; set; }
        public bool EnableComments { get; set; }
        public bool EnableCheapLabels { get; set; }
        public bool EnableUnnamedLabels { get; set; }
        public bool EnableEmbeddedRefs { get; set; }

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

            MakeProcPatternRegex();

            SortLabelsBySegment( _labelDb.Program );
            SortLabelsBySegment( _labelDb.SaveRam );
        }

        private void SortLabelsBySegment( LabelNamespace @namespace )
        {
            List<Segment> segments;

            if ( @namespace.Type == LabelType.SaveRam )
            {
                segments = _segments
                    .Where( ( s ) => s.Type == SegmentType.SaveRam )
                    .OrderBy( ( s ) => s.Address )
                    .ToList();
            }
            else if ( @namespace.Type == LabelType.Program )
            {
                segments = _segments.Where( ( s ) => s.Type == SegmentType.Program ).ToList();
            }
            else
            {
                throw new ArgumentException( "Unsupported namespace type" );
            }

            int segIndex = 0;
            int labelIndex = 0;

            while ( labelIndex < @namespace.SortedNames.Count && segIndex < segments.Count )
            {
                LabelRecord label = @namespace.SortedNames[labelIndex];
                Segment segment = segments[segIndex];

                int nsSegOffset = segment.GetNamespaceOffset( segment.Offset );
                int nsSegEndOffset = nsSegOffset + segment.Size;

                if ( nsSegEndOffset <= label.Address )
                {
                    segIndex++;
                }
                else if ( nsSegOffset <= label.Address )
                {
                    label.SegmentId = segment.Id;
                    labelIndex++;
                }
                else
                {
                    // This is a label without a segment.
                    labelIndex++;

                    // If needed, we can implement a "root segment" concept to catch
                    // all labels in a bank that are not within user-specified segments.
                }
            }
        }

        private void MakeProcPatternRegex()
        {
            if ( _config.Comments != null && !string.IsNullOrEmpty( _config.Comments.ProcPattern ) )
            {
                _procPatternRegex = new Regex( _config.Comments.ProcPattern );
            }
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
            ClearUnusedCoverageBit();
            MarkSaveRamCodeCoverage();
            GenerateSaveRamJumpLabels();

            ProcessConfigAttributes();
            ProcessCommentAttributes();
            TraceCode();

            if ( EnableUnnamedLabels )
                MarkUnnamedLabels();

            if ( EnableCheapLabels )
                CollateModuleLabels();

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
                    string sideComment = null;

                    ushort pc = (ushort) segment.GetAddress( romOffset );

                    int nsOffset = segment.GetNamespaceOffset( romOffset );

                    if ( segment.Namespace.ByAddress.TryGetValue( nsOffset, out subjLabel ) )
                    {
                        var commentParts = new CommentParts();
                        bool looksLikeProc = false;

                        if ( !string.IsNullOrEmpty( subjLabel.Comment ) )
                        {
                            var commentParser = new CommentParser( subjLabel.Comment );
                            commentParts = commentParser.ParseAll();
                            TurnAboveIntoSideComment( ref commentParts );
                            attribute = commentParts.Attribute;
                            sideComment = commentParts.Side;
                            looksLikeProc = CommentMatchesProcPattern( commentParts.Above );
                        }

                        if ( subjLabel.DataAttribute != null )
                        {
                            if ( attribute != null )
                                throw new Exception( "An attribute was specified in a label and config." );

                            attribute = subjLabel.DataAttribute;
                        }

                        if ( EnableComments && looksLikeProc && !string.IsNullOrEmpty( commentParts.Above ) )
                        {
                            FlushDataBlock( dataBlock, writer );
                            WriteProcCommentBlock( commentParts.Above, writer );
                        }

                        if ( !string.IsNullOrEmpty( subjLabel.Name ) )
                        {
                            FlushDataBlock( dataBlock, writer );
                            string labelName = subjLabel.Name;
                            if ( EnableCheapLabels || EnableUnnamedLabels )
                                labelName = ConvertAutojumpLabel( subjLabel );
                            writer.WriteLine( "{0}:", labelName );
                        }

                        if ( EnableComments )
                        {
                            if ( !looksLikeProc && !string.IsNullOrEmpty( commentParts.Above ) )
                            {
                                FlushDataBlock( dataBlock, writer );
                                WriteCommentBlock( commentParts.Above, "    ", writer );
                            }

                            if ( !string.IsNullOrEmpty( commentParts.Below ) )
                            {
                                FlushDataBlock( dataBlock, writer );
                                WriteCommentBlock( commentParts.Below, "    ", writer );
                            }
                        }
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

                        if ( inst.Mode == Mode.r
                            || (inst.Mode == Mode.a && inst.Class == Class.JMP) )
                        {
                            var operand = FindAbsoluteAddressLabel( segment, inst.Value, romOffset );

                            if ( operand != null && !string.IsNullOrEmpty( operand.Name ) )
                            {
                                if ( EnableCheapLabels || EnableUnnamedLabels )
                                    memoryName = ReferenceAutojumpLabel( operand, segment, romOffset );
                                else
                                    memoryName = operand.Name;
                            }
                        }
                        else if ( attribute is ExprCodeAttribute exprAttr )
                        {
                            memoryName = exprAttr.Expression;
                        }
                        else if ( subjLabel != null && subjLabel.OperandExpr != null )
                        {
                            memoryName = subjLabel.OperandExpr;
                        }

                        Debug.Assert( memoryName == null || memoryName.Length > 0 );

                        writer.Flush();
                        long lineStartPos = writer.BaseStream.Position;
                        writer.Write( "    " );
                        disasm.Format( inst, memoryName, writer );
                        WriteSideComment( writer, sideComment, lineStartPos );
                        writer.WriteLine();

                        if ( inst.Class == Class.JMP
                            || inst.Class == Class.RTS
                            || inst.Class == Class.RTI )
                            writer.WriteLine();

                        romOffset += instLen - 1;
                    }
                    else
                    {
                        if ( !string.IsNullOrEmpty( sideComment ) )
                        {
                            FlushDataBlock( dataBlock, writer );
                            WriteCommentBlock( sideComment, "", writer );
                        }

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
                        else if ( subjLabel != null && subjLabel.Length > 1 && !SeparateUnknown )
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

        private string ConvertAutojumpLabel( LabelRecord label )
        {
            return label.Scope switch
            {
                LabelScope.Cheap => "@" + label.CheapTag,
                LabelScope.Module => label.CheapTag,
                LabelScope.Unnamed => "",
                _ => label.Name
            };
        }

        private string ReferenceAutojumpLabel( LabelRecord label, Segment segment, int instOffset )
        {
            return label.Scope switch
            {
                LabelScope.Cheap => "@" + label.CheapTag,
                LabelScope.Module => label.CheapTag,
                LabelScope.Unnamed => ReferenceUnnamedLabel( label, segment, instOffset ),
                _ => label.Name
            };
        }

        private string ReferenceUnnamedLabel( LabelRecord label, Segment segment, int instOffset )
        {
            int targetIndex = segment.Namespace.Autojump.IndexOfKey( label.Address );

            if ( targetIndex < 0 )
                throw new Exception( string.Format( "Auto-jump label wasn't found: {0}", label.Name ) );

            int targetOffset = segment.GetRomOffsetFromNSOffset( label.Address );
            int distance = 0;

            if ( targetOffset <= instOffset )
            {
                for ( ; targetOffset <= instOffset; distance-- )
                {
                    targetIndex++;
                    if ( targetIndex < segment.Namespace.Autojump.Count )
                    {
                        int nsOffset = segment.Namespace.Autojump.Values[targetIndex].Address;
                        targetOffset = segment.GetRomOffsetFromNSOffset( nsOffset );
                    }
                    else
                    {
                        targetOffset = int.MaxValue;
                    }
                }
            }
            else
            {
                for ( ; targetOffset > instOffset; distance++ )
                {
                    targetIndex--;
                    if ( targetIndex >= 0 )
                    {
                        int nsOffset = segment.Namespace.Autojump.Values[targetIndex].Address;
                        targetOffset = segment.GetRomOffsetFromNSOffset( nsOffset );
                    }
                    else
                    {
                        targetOffset = -1;
                    }
                }
            }

            _unnamedLabelStringBuilder.Clear();
            _unnamedLabelStringBuilder.Append( ':' );

            if ( distance < 0 )
                _unnamedLabelStringBuilder.Append( '-', -distance );
            else
                _unnamedLabelStringBuilder.Append( '+', distance );

            return _unnamedLabelStringBuilder.ToString();
        }

        private void TurnAboveIntoSideComment( ref CommentParts parts )
        {
            // Turn a single-line upper section into a side section.

            if ( parts.Side == null && parts.Above != null && !parts.Above.Contains( "\\n" ) )
            {
                parts.Side = parts.Above;
                parts.Above = null;
            }
        }

        private void WriteSideComment( StreamWriter writer, string comment, long linePos )
        {
            if ( !EnableComments || string.IsNullOrEmpty( comment ) )
                return;

            writer.Flush();

            int lineLength = (int) (writer.BaseStream.Position - linePos);
            int pad;

            if ( lineLength < 32 )
                pad = 32 - lineLength;
            else
                pad = 4;

            for ( int i = 0; i < pad; i++ )
            {
                writer.Write( ' ' );
            }

            writer.Write( ';' );
            writer.Write( comment );
        }

        private static void WriteCommentBlock( string comment, string indent, StreamWriter writer )
        {
            string[] lines = comment.Split( "\\n" );

            foreach ( var line in lines )
            {
                writer.WriteLine( "{0};{1}", indent, line );
            }
        }

        private void WriteProcCommentBlock( string comment, StreamWriter writer )
        {
            WriteCommentBlock( comment, "", writer );
        }

        private bool CommentMatchesProcPattern( string comment )
        {
            return !string.IsNullOrEmpty( comment )
                && _procPatternRegex != null
                && _procPatternRegex.IsMatch( comment );
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

                writer.WriteLine( "    .ADDR {0}", sOperand );
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

        private (int, LabelNamespace) GetAbsoluteAddressNamespaceOffset(
            Segment segment, ushort addr, int instOffset )
        {
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

            return (absOffset, labelNamespace);
        }

        private LabelRecord FindAbsoluteAddressLabel( Segment segment, ushort addr, int instOffset )
        {
            var (absOffset, labelNamespace) =
                GetAbsoluteAddressNamespaceOffset( segment, addr, instOffset );

            if ( labelNamespace.ByAddress.TryGetValue( absOffset, out var record ) )
                return record;

            return null;
        }

        private (LabelRecord, int) FindAbsoluteOrOffsetLabel( Segment segment, ushort addr, int instOffset )
        {
            var (absOffset, labelNamespace) =
                GetAbsoluteAddressNamespaceOffset( segment, addr, instOffset );

            if ( labelNamespace.ByAddress.TryGetValue( absOffset, out var record ) )
                return (record, 0);

            _blockLabelComparer.NSOffset = absOffset;

            int index = labelNamespace.SortedNames.BinarySearch( null, _blockLabelComparer );

            if ( index < 0 )
                return (null, 0);

            int offset = absOffset - labelNamespace.SortedNames[index].Address;

            return (labelNamespace.SortedNames[index], offset);
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

        private static bool IsReferenceToSwitchableBank( InstDisasm inst )
        {
            return (inst.Class != Class.STA && inst.Class != Class.STX && inst.Class != Class.STY)
                && IsAbsolute( inst.Mode ) && inst.Value >= 0x8000 && inst.Value < 0xC000;
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

            CollateImports();
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
                                    ValidateJumpTable( tableOffset, record );

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

        private void CollateImports()
        {
            Segment callerSeg;

            foreach ( var bankInfo in _config.Banks )
            {
                foreach ( var segment in bankInfo.Segments )
                {
                    int endOffset = segment.Offset + segment.Size;
                    int addr = segment.Address;

                    callerSeg = segment;
                    ProcessBankCode( segment.Offset, endOffset, addr, ProcessInstruction );
                    ProcessDataImports( segment );
                }
            }

            void ProcessInstruction( InstDisasm inst, int romOffset )
            {
                LabelRecord label = null;

                if ( IsZeroPage( inst.Mode )
                    || (IsAbsolute( inst.Mode ) && !WritesToRom( inst )) )
                {
                    int opOffset = 0;

                    if ( EnableEmbeddedRefs )
                        (label, opOffset) = FindAbsoluteOrOffsetLabel( callerSeg, inst.Value, romOffset );
                    else
                        label = FindAbsoluteAddressLabel( callerSeg, inst.Value, romOffset );

                    if ( !string.IsNullOrEmpty( label?.Name ) )
                    {
                        int nsOffset = callerSeg.GetNamespaceOffset( romOffset );
                        LabelRecord instLabel;

                        if ( !callerSeg.Namespace.ByAddress.TryGetValue( nsOffset, out instLabel ) )
                        {
                            instLabel = new LabelRecord()
                            {
                                Address = nsOffset,
                                Length = 1,
                                SegmentId = callerSeg.Id,
                                Type = callerSeg.Namespace.Type,
                            };

                            callerSeg.Namespace.ByAddress.Add( instLabel.Address, instLabel );
                        }

                        if ( opOffset == 0 )
                            instLabel.OperandExpr = label.Name;
                        else
                            instLabel.OperandExpr = string.Format( "{0}+{1}", label.Name, opOffset );
                    }
                }

                if ( label != null && label.SegmentId >= 0 )
                {
                    Segment calleeSeg = _segments[label.SegmentId];

                    if ( calleeSeg.Parent != callerSeg.Parent )
                    {
                        AddImport( callerSeg, label, source: calleeSeg );
                        AddExport( calleeSeg, label );
                    }
                }
            }
        }

        private void ProcessDataImports( Segment callerSeg )
        {
            int endOffset = callerSeg.Offset + callerSeg.Size;

            for ( int offset = callerSeg.Offset; offset < endOffset; offset++ )
            {
                if ( (_coverage[offset] & 0x11) == 0 && (_tracedCoverage[offset] & 0x40) != 0 )
                {
                    int nsOffset = callerSeg.GetNamespaceOffset( offset );

                    if ( callerSeg.Namespace.ByAddress.TryGetValue( nsOffset, out var tableLabel ) )
                    {
                        // TODO: Assumes that there are no imports in addr tables in RAM segments

                        if ( callerSeg.Type == SegmentType.SaveRam || callerSeg.Address >= 0xC000 )
                        {
                            PortFixedSegmentAddressTable( callerSeg, tableLabel, offset );
                        }
                        else
                        {
                            PortSwitchableSegmentAddressTable( callerSeg, tableLabel, offset );
                        }

                        offset += tableLabel.Length - 1;
                    }
                }
            }
        }

        private void PortFixedSegmentAddressTable( Segment callerSeg, LabelRecord tableLabel, int offset )
        {
            byte[] image = _rom.Image;

            int bank = _tracedCoverage[offset] & 0x0F;
            Bank bankInfo = _config.Banks[bank];

            for ( int i = 0; i < tableLabel.Length; i += 2, offset += 2 )
            {
                ushort addr = (ushort) (image[offset] | (image[offset + 1] << 8));

                if ( addr >= 0x8000 && addr < 0xC000 )
                {
                    var entryLabel = FindAbsoluteAddressLabel( callerSeg, addr, offset );

                    if ( entryLabel != null && !string.IsNullOrEmpty( entryLabel.Name ) )
                    {
                        Segment calleeSeg = FindSegmentByAddress( bankInfo, addr );

                        AddImport( callerSeg, entryLabel, calleeSeg );
                        AddExport( calleeSeg, entryLabel );
                    }
                }
            }
        }

        private void PortSwitchableSegmentAddressTable( Segment callerSeg, LabelRecord tableLabel, int offset )
        {
            int tableOffset = offset;

            for ( int i = 0; i < tableLabel.Length; i += 2 )
            {
                int o = tableOffset + i;
                ushort entryAddr = (ushort) (_rom.Image[o] | (_rom.Image[o + 1] << 8));

                if ( !callerSeg.IsAddressInside( entryAddr ) )
                {
                    Segment calleeSeg = FindFixedSegmentByAddress( entryAddr );

                    if ( calleeSeg != null && calleeSeg.Parent != callerSeg.Parent )
                    {
                        int target = GetOffsetFromAddress( calleeSeg, entryAddr );

                        int nsTargetOffset = calleeSeg.GetNamespaceOffset( target );

                        if ( calleeSeg.Namespace.ByAddress.TryGetValue( nsTargetOffset, out var record ) )
                        {
                            AddImport( callerSeg, record, source: calleeSeg );
                            AddExport( calleeSeg, record );
                        }
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

        private static int GetOffsetFromAddress( Bank bankInfo, int address )
        {
            return address - bankInfo.Address + bankInfo.Offset;
        }

        private static int GetOffsetFromAddress( Segment segment, int address )
        {
            if ( segment.Type == SegmentType.Program )
                return address - segment.Parent.Address + segment.Parent.Offset;
            else
                return address - segment.Address + segment.Offset;
        }

        private static Segment FindSegmentByAddress( Bank bankInfo, int address )
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
                if ( IsReferenceToSwitchableBank( inst ) )
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
                    if ( segment.Type != SegmentType.Program || segment.Address >= 0xC000 )
                        continue;

                    int endOffset = segment.Offset + segment.Size;
                    int addr = segment.Address;

                    callerSeg = segment;
                    ProcessBankCode( segment.Offset, endOffset, addr, ProcessInstruction );
                }
            }

            void ProcessInstruction( InstDisasm inst, int offset )
            {
                if ( inst.Class != Class.JMP && inst.Class != Class.JSR )
                    return;

                Segment calleeSeg = null;

                if ( inst.Class == Class.JSR && inst.Value == 0xE5E2 )
                {
                    int instLen = Disasm6502.Disassembler.GetInstructionLengthByMode( inst.Mode );
                    int tableOffset = offset + instLen;
                    LabelRecord tableLabel;

                    if ( _labelDb.Program.ByAddress.TryGetValue( tableOffset, out tableLabel ) )
                    {
                        ValidateJumpTable( tableOffset, tableLabel );

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

                                    _tracedCoverage[target] = (byte) (bankNumber | 0x20);
                                    _originCoverage[target] = offset;
                                }
                            }
                        }

                        _tracedCoverage[tableOffset] |= 0x40;
                    }
                }

                calleeSeg = FindFixedSegmentByAddress( inst.Value );

                if ( calleeSeg == null || calleeSeg.Id == callerSeg.Id )
                    return;

                int targetOffset = GetOffsetFromAddress( calleeSeg, inst.Value );

                if ( (inst.Class == Class.JMP || inst.Class == Class.JSR)
                    && inst.Mode == Mode.a )
                {
                    _tracedCoverage[targetOffset] = (byte) (bankNumber | 0x20);
                    _originCoverage[targetOffset] = offset;
                }
            }
        }

        private static void ValidateJumpTable( int tableOffset, LabelRecord tableLabel )
        {
            if ( tableLabel.Length == 0 || (tableLabel.Length % 2) != 0 )
            {
                string message = string.Format(
                    "Jump table must have an even number of bytes at least 2: {0}",
                    tableLabel.Name );
                throw new Exception( message );
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
                    bool addName = false;

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
                        addName = true;
                    }
                    else if ( string.IsNullOrEmpty( record.Name ) )
                    {
                        record.Name = labelName;
                        addName = true;
                    }

                    if ( addName )
                    {
                        _labelDb.SaveRam.ByName.Add( record.Name, record );

                        if ( LabelDatabase.PlainAutojumpRegex.IsMatch( record.Name ) )
                            _labelDb.SaveRam.Autojump.Add( record.Address, record );
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

        private void ProcessCommentAttributes()
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

        private void ProcessConfigAttributes()
        {
            var allowedAttributes = new Dictionary<string, Type>()
            {
                { "INCBIN", typeof( IncBinDataAttribute ) },
                { "ATAB", typeof( AddrTableDataAttribute ) },
                { "ATABL", typeof( SplitAddrTableLoDataAttribute ) },
                { "ATABH", typeof( SplitAddrTableHiDataAttribute ) },
                { "WORD", typeof( WordDataAttribute ) },
            };

            foreach ( var attrConfig in _config.Attributes )
            {
                Type attrType = allowedAttributes[attrConfig.Name];

                LabelNamespace @namespace = _labelDb.GetNamespace( attrConfig.Namespace[0] );

                if ( @namespace == null )
                    throw new Exception();

                LabelRecord label = @namespace.ByAddress[attrConfig.Offset];

                if ( label.SegmentId < 0 )
                    throw new Exception();

                var dataAttr = (DataAttribute) Activator.CreateInstance(
                    attrType, attrConfig.Content, 0, attrConfig.Content.Length );

                Segment segment = _segments[label.SegmentId];
                dataAttr.ProcessBlock( this, segment, attrConfig.Offset, label );

                // Attach it to the label here, so that it can be used to write later.
                label.DataAttribute = dataAttr;
            }
        }

        private void MarkUnnamedLabels()
        {
            var autojumpLists = new[] { _labelDb.Program.Autojump, _labelDb.SaveRam.Autojump };

            foreach ( var list in autojumpLists )
            {
                foreach ( var label in list.Values )
                {
                    label.Scope = LabelScope.Unnamed;
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
            using var writer = new StreamWriter( "Z.cfg", false, System.Text.Encoding.ASCII );

            writer.WriteLine( "MEMORY\n{" );
            foreach ( var bankInfo in _config.Banks )
            {
                writer.WriteLine(
                    "    ROM_{0}: start = ${1:X4}, size = ${2:X4}, file = %O, fill = yes, fillval = $FF ;",
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


        #region Inner classes

        private class BlockLabelComparer : IComparer<LabelRecord>
        {
            public int NSOffset { get; set; }

            public int Compare( LabelRecord x, LabelRecord y )
            {
                if ( x.Address < NSOffset )
                {
                    if ( (x.Address + x.Length) <= NSOffset )
                        return -1;

                    return 0;
                }
                else if ( x.Address == NSOffset )
                {
                    return 0;
                }
                else
                {
                    return 1;
                }
            }
        }

        #endregion


        #region ModuleLabels

        [DebuggerDisplay( "{Label?.Name}, {Begin}..{End}" )]
        private class CheapRange
        {
            public LabelRecord Label;
            public int Begin;
            public int End;
            public CheapRange[] Links;
        }

        private List<CheapRange> _ranges;
        private Queue<CheapRange> _rangeQ;

        private void CollateModuleLabels()
        {
            foreach ( var segment in _segments )
            {
                CollateSegmentModuleLabels( segment );
            }
        }

        private void CollateSegmentModuleLabels( Segment segment )
        {
            _ranges = new();
            _rangeQ = new();

            FindCheapRanges( segment );
            LinkCheapRanges( segment );
            FindModuleLabels( segment );
        }

        private void FindCheapRanges( Segment segment )
        {
            var labelToRange = new Dictionary<LabelRecord, CheapRange>();
            int endOffset = segment.Offset + segment.Size;

            ProcessBankCode( segment.Offset, endOffset, segment.Address, ProcessInstruction );

            void ProcessInstruction( InstDisasm inst, int romOffset )
            {
                int nsOffset = segment.GetNamespaceOffset( romOffset );

                if ( segment.Namespace.ByAddress.TryGetValue( nsOffset, out var instLabel )
                    && !string.IsNullOrEmpty( instLabel.Name ) )
                {
                    string cheapTag = null;
                    LabelScope scope = LabelScope.Unknown;

                    if ( instLabel.Scope != LabelScope.Unnamed )
                    {
                        Match match = LabelDatabase.AutojumpLabelRegex.Match( instLabel.Name );

                        if ( match.Success && match.Groups[1].Success )
                        {
                            scope = LabelScope.Cheap;
                            cheapTag = match.Groups[1].Value;
                        }
                        else
                        {
                            scope = LabelScope.Full;
                        }
                    }

                    if ( scope != LabelScope.Unknown )
                    {
                        AddOrUpdateRange( romOffset, instLabel );

                        instLabel.Scope = scope;
                        instLabel.CheapTag = cheapTag;
                    }
                }

                if ( (inst.Mode == Mode.r
                    || (inst.Mode == Mode.a && inst.Class == Class.JMP))
                    && segment.IsAddressInside( inst.Value ) )
                {
                    int targetOffset = GetOffsetFromAddress( segment, inst.Value );
                    int nsTargetOffset = segment.GetNamespaceOffset( targetOffset );

                    if ( segment.Namespace.ByAddress.TryGetValue( nsTargetOffset, out var targetLabel )
                        && !string.IsNullOrEmpty( targetLabel.Name ) )
                    {
                        Match match = LabelDatabase.AutojumpLabelRegex.Match( targetLabel.Name );

                        if ( match.Success && match.Groups[1].Success )
                        {
                            AddOrUpdateRange( romOffset + 1, targetLabel );
                        }
                    }
                }

                void AddOrUpdateRange( int romOffset, LabelRecord label )
                {
                    CheapRange range;

                    if ( !labelToRange.TryGetValue( label, out range ) )
                    {
                        range = new CheapRange();
                        range.Label = label;
                        range.Begin = romOffset;
                        labelToRange.Add( label, range );
                        _ranges.Add( range );
                    }

                    if ( range.End < romOffset )
                        range.End = romOffset;
                }
            }
        }

        private void LinkCheapRanges( Segment segment )
        {
            if ( _ranges.Count == 0 )
                return;

            int begin = _ranges[0].Begin;
            int end = _ranges[^1].End;

            List<CheapRange> curRanges = new();
            int nextRangeIndex = 0;

            for ( int romOffset = begin; romOffset <= end; romOffset++ )
            {
                for ( int i = curRanges.Count - 1; i >= 0; i-- )
                {
                    CheapRange r = curRanges[i];

                    if ( romOffset == r.Label.Address )
                        r.Links = curRanges.ToArray();

                    if ( romOffset == r.End )
                        curRanges.RemoveAt( i );
                }

                while ( nextRangeIndex < _ranges.Count
                    && _ranges[nextRangeIndex].Begin == romOffset )
                {
                    CheapRange r = _ranges[nextRangeIndex];

                    if ( romOffset == r.Label.Address )
                        r.Links = curRanges.ToArray();

                    if ( r.Label.Scope == LabelScope.Cheap )
                        curRanges.Add( r );
                    else
                        _rangeQ.Enqueue( r );

                    nextRangeIndex++;
                }
            }
        }

        private void FindModuleLabels( Segment segment )
        {
            var modNames = new HashSet<string>();

            while ( _rangeQ.Count > 0 )
            {
                CheapRange range = _rangeQ.Dequeue();

                if ( range.Links != null )
                {
                    foreach ( var link in range.Links )
                    {
                        if ( link.Label.Scope == LabelScope.Cheap )
                        {
                            if ( !segment.Namespace.ByName.ContainsKey( link.Label.CheapTag )
                                && !modNames.Contains( link.Label.CheapTag ) )
                            {
                                link.Label.Scope = LabelScope.Module;
                                modNames.Add( link.Label.CheapTag );
                            }
                            else
                            {
                                link.Label.Scope = LabelScope.Full;
                            }

                            _rangeQ.Enqueue( link );
                        }
                    }
                }
            }
        }

        #endregion
    }
}
