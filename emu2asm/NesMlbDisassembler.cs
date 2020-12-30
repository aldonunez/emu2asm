﻿using System;
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

            //foreach ( var pair in _labelDb.SaveRam.ByName )
            //{
            //    uint address = pair.Value.Address;

            //    builder.AppendFormat( "{0} := {1}\n", pair.Key, address );
            //}

            string definitions = builder.ToString();

            foreach ( var bank in _config.Banks )
            {
                DisassembleBank( bank, definitions );
            }
        }

        private void DisassembleBank( Bank bankInfo, string definitions )
        {
            var disasm = new Disasm6502.Disassembler();
            var dataBlock = new DataBlock();

            // TODO: delete all ASM files beforehand?

            string filename = string.Format( "Z_{0}.asm", bankInfo.Id );

            using var writer = new StreamWriter( filename, false, System.Text.Encoding.ASCII );

            writer.WriteLine();
            writer.WriteLine( ".SEGMENT \"BANK_{0}\"", bankInfo.Id );
            writer.WriteLine();
            writer.WriteLine( definitions );

            int endOffset = bankInfo.Offset + bankInfo.Size;

            for ( int i = bankInfo.Offset; i < endOffset; i++ )
            {
                byte c = _coverage[i];

                int offset = i;
                ushort pc = (ushort) (0x8000 + ((uint) offset % 0x4000));

                if ( _labelDb.Program.ByAddress.TryGetValue( offset, out var record ) )
                {
                    if ( !string.IsNullOrEmpty( record.Name ) )
                    {
                        FlushDataBlock( dataBlock, writer );

                        writer.WriteLine( "{0}:", record.Name );
                    }

                    // TODO: comments
                }

                if ( (c & 0x11) != 0 )
                {
                    FlushDataBlock( dataBlock, writer );

                    disasm.PC = pc;
                    InstDisasm inst = disasm.Disassemble( _rom.Image, i );
                    string memoryName = null;

                    int instLen = Disasm6502.Disassembler.GetInstructionLengthByMode( inst.Mode );

                    if ( instLen < 1 )
                    {
                        string message = string.Format( "Found a bad instruction at program offset {0:X5}", offset );
                        throw new ApplicationException( message );
                    }

                    i += instLen - 1;

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
                        dataBlock.Offset = offset;
                        dataBlock.Known = (c & 0x22) != 0;
                    }

                    dataBlock.Size++;
                }
            }

            FlushDataBlock( dataBlock, writer );
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

        // Mark the code bytes that we were given.

        private void TraceCode()
        {
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
    }
}
