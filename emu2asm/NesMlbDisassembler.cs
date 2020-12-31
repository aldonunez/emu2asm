using System;
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
        }

        public void Disassemble()
        {
            TraceCode();

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

                if ( address < 0x67F0 || address >= 0x7F00 )
                {
                    builder.AppendFormat( "{0} := {1}\n", pair.Key, address );
                }
                else
                {
                    // TODO: IMPORT as needed

                    builder.AppendFormat( ".GLOBAL {0}\n", pair.Key );
                }
            }

            builder.AppendLine();

            foreach ( var pair in _labelDb.Program.ByName )
            {
                // TODO:

                if ( pair.Value.Address < 0x1C000 )
                    continue;

                // TODO: IMPORT as needed

                builder.AppendFormat( ".GLOBAL {0}\n", pair.Key );
            }

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

                    int instLen = Disasm6502.Disassembler.GetInstructionLengthByMode( inst.Mode );

                    if ( instLen < 1 )
                    {
                        string message = string.Format( "Found a bad instruction at program offset {0:X5}", romOffset );
                        throw new ApplicationException( message );
                    }

                    romOffset += instLen - 1;

                    if ( IsAbsolute( inst.Mode ) || IsZeroPage( inst.Mode ) || inst.Mode == Mode.r )
                    {
                        record = FindAbsoluteAddress( bankInfo, inst );

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

        private LabelRecord FindAbsoluteAddress( Bank bankInfo, InstDisasm inst )
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
                absOffset = (inst.Value - 0x8000) + bankInfo.Offset;
                labelNamespace = _labelDb.Program;
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

        private enum TracedOrigin : byte
        {
            Unknown,
            Evident,
            Traced
        }

        private const byte TracedOriginMask = 3;
        private const byte TracedBankKnownFlag = 4;
        private const byte TracedBankMask = 0x38;
        private const byte TracedBankShift = 3;

        private void TraceCode()
        {
            // Mark the code bytes that we were given.

            for ( int i = 0; i < _coverage.Length; i++ )
            {
                byte c = _coverage[i];

                // 0x11: Code and code accessed indirectly

                if ( (c & 0x11) != 0 )
                {
                    uint t = (uint) TracedOrigin.Evident;
                    uint bank = (uint) i >> 14;

                    if ( bank < 7 )
                    {
                        t |= (bank << TracedBankShift);
                        t |= TracedBankKnownFlag;
                    }

                    _tracedCoverage[i] = (byte) t;
                }
            }

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

            // Generate labels for jumps in code copied to Save RAM.

            var disasm = new Disasm6502.Disassembler();

            foreach ( var bankInfo in _config.Banks )
            {
                if ( bankInfo.RomToRam == null )
                    continue;

                var romToRam = bankInfo.RomToRam;
                int startOffset = (bankInfo.RomToRam.RomAddress - bankInfo.Address) + bankInfo.Offset;
                int endOffset = startOffset + bankInfo.RomToRam.Size;
                int endRamAddr = romToRam.RamAddress + romToRam.Size;

                int addr = romToRam.RamAddress;

                if ( bankInfo.RomToRam.Type == MemoryUse.Data )
                {
                    Array.Fill<byte>( _coverage, 0x02, startOffset, romToRam.Size );
                    continue;
                }

                for ( int offset = startOffset; offset < endOffset; offset++ )
                {
                    byte c = _coverage[offset];

                    if ( (c & 0x11) != 0 )
                    {
                        disasm.PC = (ushort) addr;
                        InstDisasm inst = disasm.Disassemble( _rom.Image, offset );

                        int instLen = Disasm6502.Disassembler.GetInstructionLengthByMode( inst.Mode );

                        if ( instLen < 1 )
                        {
                            string message = string.Format( "Found a bad instruction at program offset {0:X5}", offset );
                            throw new ApplicationException( message );
                        }

                        offset += instLen - 1;
                        addr += instLen;

                        if ( inst.Mode == Mode.r )
                        {
                            int relAddr = inst.Value - 0x6000;
                            string labelName = string.Format( "L{0:X4}", inst.Value );

                            // If found, then it has a comment but no name.

                            if ( !_labelDb.SaveRam.ByName.TryGetValue( labelName, out LabelRecord record ) )
                            {
                                if ( !_labelDb.SaveRam.ByAddress.TryGetValue( relAddr, out record ) )
                                {
                                    record = new LabelRecord
                                    {
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
                    else
                    {
                        addr++;
                    }
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
    }
}
