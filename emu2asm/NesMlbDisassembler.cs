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
            MarkEvidentCode();
            TraceCode();

            var disasm = new Disasm6502.Disassembler();
            uint prevBank = uint.MaxValue;
            uint dataOffset = 0;
            uint dataRun = 0;

            // TODO: delete all ASM files beforehand?

            StreamWriter writer = null;

            // TODO: Only include definitions that are used in each bank.

            StringBuilder builder = new StringBuilder();

            foreach ( var pair in _labelDb.Ram.ByName )
            {
                uint address = pair.Value.Address;

                builder.AppendFormat( "{0} := ${1:X2}\n", pair.Key, address );
            }

            foreach ( var pair in _labelDb.Registers.ByName )
            {
                uint address = pair.Value.Address;

                builder.AppendFormat( "{0} := ${1:X2}\n", pair.Key, address );
            }

            //foreach ( var pair in _labelDb.SaveRam.ByName )
            //{
            //    uint address = pair.Value.Address;

            //    builder.AppendFormat( "{0} := {1}\n", pair.Key, address );
            //}

            string definitions = builder.ToString();

            try
            {
                for ( int i = 0; i < _coverage.Length; i++ )
                {
                    byte c = _coverage[i];

                    uint offset = (uint) i;
                    uint bank = offset >> 14;
                    ushort pc = (ushort) (0x8000 + (offset % 0x4000));

                    if ( bank == 7 )
                        break;

                    if ( bank == 4 && pc == 0x801E)
                        Debugger.Break();

                    if ( bank != prevBank )
                    {
                        if ( dataRun > 0 )
                        {
                            WriteDataBlock( dataOffset, dataRun, writer );
                            dataRun = 0;
                        }

                        if ( writer != null )
                            writer.Close();

                        string filename = string.Format( "Z_{0:D2}.asm", bank );
                        writer = new StreamWriter( filename, false, System.Text.Encoding.ASCII );

                        writer.WriteLine();
                        writer.WriteLine( ".SEGMENT \"BANK_{0:X2}\"\n", bank );

                        writer.WriteLine( definitions );

                        prevBank = bank;
                    }

                    if ( _labelDb.Program.ByAddress.TryGetValue( offset, out var record ) )
                    {
                        if ( !string.IsNullOrEmpty( record.Name ) )
                        {
                            if ( dataRun > 0 )
                            {
                                WriteDataBlock( dataOffset, dataRun, writer );
                                dataRun = 0;
                            }

                            writer.WriteLine( "{0}:", record.Name );
                        }

                        // TODO: comments
                    }

                    if ( (c & 0x11) != 0 )
                    {
                        disasm.PC = pc;
                        InstDisasm inst = disasm.Disassemble( _rom.Image, i );
                        string memoryName = null;

                        int instLen = Disasm6502.Disassembler.GetInstructionLengthByMode( inst.Mode );
                        i += instLen - 1;

                        if ( IsAbsolute( inst.Mode ) || IsZeroPage( inst.Mode ) )
                        {
                            record = FindAbsoluteAddress( bank, inst );

                            if ( record != null && !string.IsNullOrEmpty( record.Name ) )
                                memoryName = record.Name;
                        }
                        // TODO: roll this into above
                        else if ( inst.Mode == Mode.r )
                        {
                            record = FindAbsoluteAddress( bank, inst );

                            if ( record != null && !string.IsNullOrEmpty( record.Name ) )
                            {
                                memoryName = record.Name;
                            }
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
                        //if ( (c & 0x22) == 0 )
                        //    writer.WriteLine( "; Unknown block" );

                        if ( dataRun == 0 )
                            dataOffset = offset;

                        dataRun++;
                    }
                }
            }
            finally
            {
                if ( writer != null )
                    writer.Close();
            }
        }

        private LabelRecord FindAbsoluteAddress( uint bank, InstDisasm inst )
        {
            LabelRecord record;
            LabelNamespace labelNamespace;
            uint absOffset;

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
                absOffset = (inst.Value - 0x6000u);
                labelNamespace = _labelDb.SaveRam;
            }
            else if ( inst.Value < 0xC000 )
            {
                absOffset = (inst.Value - 0x8000u) + bank * 0x4000;
                labelNamespace = _labelDb.Program;
            }
            else
            {
                absOffset = (inst.Value - 0xC000u) + 0x1C000;
                labelNamespace = _labelDb.Program;
            }

            if ( labelNamespace.ByAddress.TryGetValue( absOffset, out record ) )
                return record;

            return null;
        }

        private void WriteDataBlock( uint start, uint length, StreamWriter writer )
        {
            while ( length > 0 )
            {
                writer.Write( "    .BYTE " );

                uint lengthToWrite = (length > 8) ? 8 : length;

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

        private void MarkEvidentCode()
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
        }

        private void TraceCode()
        {
        }
    }
}
