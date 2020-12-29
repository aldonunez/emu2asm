using System;
using System.IO;

namespace Disasm6502
{
    public enum Class : byte
    {
        None,
        LDA,
        LDX,
        LDY,
        STA,
        STX,
        STY,
        ADC,
        SBC,
        INC,
        INX,
        INY,
        DEC,
        DEX,
        DEY,
        ASL,
        LSR,
        ROL,
        ROR,
        AND,
        ORA,
        EOR,
        CMP,
        CPX,
        CPY,
        BIT,
        BCC,
        BCS,
        BEQ,
        BMI,
        BNE,
        BPL,
        BVC,
        BVS,
        TAX,
        TXA,
        TAY,
        TYA,
        TSX,
        TXS,
        PHA,
        PLA,
        PHP,
        PLP,
        JMP,
        JSR,
        RTS,
        RTI,
        SEC,
        SED,
        SEI,
        CLC,
        CLD,
        CLI,
        CLV,
        NOP,
        BRK,
        Error,
    }

    public enum Mode : byte
    {
        None,
        A,      // Accumulator
        i,      // Implied
        I,      // Immediate
        a,      // Absolute
        aN,     // Absolute Indirect
        zp,     // Zero Page
        r,      // Relative
        ax,     // Absolute Indexed with X
        ay,     // Absolute Indexed with Y
        zpx,    // Zero Page Indexed with X
        zpy,    // Zero Page Indexed with Y
        zpxN,   // Zero Page Indexed Indirect (with X)
        zpNy    // Zero Page Indirect Indexed (with Y)
    }

    public struct InstDisasm
    {
        public Class Class;
        public Mode Mode;
        public ushort Value;
    }

    internal struct InstDesc
    {
        public Class Class;
        public Mode Mode;
    }

    [Flags]
    internal enum Registers
    {
        None,
        A = 1,
        X = 2,
        Y = 4,
        S = 8,
        P = 16,
    }

    public class Disassembler
    {
        private ushort mPC;

        public ushort PC
        {
            get { return mPC; }
            set { mPC = value; }
        }

        public static int GetInstructionLengthByMode( Mode mode )
        {
            return sModeLen[(int) mode];
        }

        public InstDisasm Disassemble( byte[] block, int index )
        {
            int opCode = block[index];

            if ( opCode >= sInstTable.Length )
                return new InstDisasm { Class = Class.None };

            var desc = sInstTable[opCode];
            var disasm = new InstDisasm();

            disasm.Class = desc.Class;
            disasm.Mode = desc.Mode;

            int newPC = mPC + sModeLen[(int) desc.Mode];

            if ( newPC >= ushort.MaxValue )
                throw new ApplicationException();

            mPC = (ushort) newPC;

            switch ( desc.Mode )
            {
                case Mode.r:
                    if ( block.Length - index < 2 )
                    {
                        disasm.Class = Class.Error;
                    }
                    else
                    {
                        sbyte sb = (sbyte) block[index + 1];
                        disasm.Value = (ushort) (mPC + sb);
                    }
                    break;

                case Mode.I:
                case Mode.zp:
                case Mode.zpx:
                case Mode.zpy:
                case Mode.zpxN:
                case Mode.zpNy:
                    if ( block.Length - index < 2 )
                        disasm.Class = Class.Error;
                    else
                        disasm.Value = block[index + 1];
                    break;

                case Mode.a:
                case Mode.aN:
                case Mode.ax:
                case Mode.ay:
                    if ( block.Length - index < 3 )
                        disasm.Class = Class.Error;
                    else
                        disasm.Value = (ushort) (block[index + 1] | (block[index + 2] << 8));
                    break;
            }

            return disasm;
        }

        public void Format( InstDisasm inst, string memoryName, TextWriter writer )
        {
            writer.Write( inst.Class );

            switch ( inst.Mode )
            {
                case Mode.A:
                case Mode.i:
                    // No operands
                    break;

                case Mode.I:
                    writer.Write( " #${0:X2}", inst.Value );
                    break;

                case Mode.zp:
                    if ( memoryName == null )
                        writer.Write( " ${0:X2}", inst.Value );
                    else
                        writer.Write( " {0}", memoryName );
                    break;

                case Mode.r:
                    if ( memoryName == null )
                        writer.Write( " ${0:X4}", inst.Value );
                    else
                        writer.Write( " {0}", memoryName );
                    break;

                case Mode.zpx:
                    if ( memoryName == null )
                        writer.Write( " ${0:X2}, X", inst.Value );
                    else
                        writer.Write( " {0}, X", memoryName );
                    break;

                case Mode.zpy:
                    if ( memoryName == null )
                        writer.Write( " ${0:X2}, Y", inst.Value );
                    else
                        writer.Write( " {0}, Y", memoryName );
                    break;

                case Mode.zpxN:
                    if ( memoryName == null )
                        writer.Write( " (${0:X2}, X)", inst.Value );
                    else
                        writer.Write( " ({0}, X)", memoryName );
                    break;

                case Mode.zpNy:
                    if ( memoryName == null )
                        writer.Write( " (${0:X2}), Y", inst.Value );
                    else
                        writer.Write( " ({0}), Y", memoryName );
                    break;

                case Mode.a:
                    if ( memoryName == null )
                        writer.Write( " ${0:X4}", inst.Value );
                    else
                        writer.Write( " {0}", memoryName );
                    break;

                case Mode.aN:
                    if ( memoryName == null )
                        writer.Write( " (${0:X4})", inst.Value );
                    else
                        writer.Write( " ({0})", memoryName );
                    break;

                case Mode.ax:
                    if ( memoryName == null )
                        writer.Write( " ${0:X4}, X", inst.Value );
                    else
                        writer.Write( " {0}, X", memoryName );
                    break;

                case Mode.ay:
                    if ( memoryName == null )
                        writer.Write( " ${0:X4}, Y", inst.Value );
                    else
                        writer.Write( " {0}, Y", memoryName );
                    break;
            }
        }

        #region Instruction tables

        private static Registers[] sClassRegs =
        {
            Registers.None,             // None
            Registers.A,                // LDA
            Registers.X,                // LDX
            Registers.Y,                // LDY
            Registers.A,                // STA
            Registers.X,                // STX
            Registers.Y,                // STY
            Registers.A,                // ADC
            Registers.A,                // SBC
            Registers.None,             // INC
            Registers.X,                // INX
            Registers.Y,                // INY
            Registers.None,             // DEC
            Registers.X,                // DEX
            Registers.Y,                // DEY
            Registers.A,                // ASL
            Registers.A,                // LSR
            Registers.A,                // ROL
            Registers.A,                // ROR
            Registers.A,                // AND
            Registers.A,                // ORA
            Registers.A,                // EOR
            Registers.A,                // CMP
            Registers.X,                // CPX
            Registers.Y,                // CPY
            Registers.A,                // BIT
            Registers.None,             // BCC
            Registers.None,             // BCS
            Registers.None,             // BEQ
            Registers.None,             // BMI
            Registers.None,             // BNE
            Registers.None,             // BPL
            Registers.None,             // BVC
            Registers.None,             // BVS
            Registers.A | Registers.X,  // TAX
            Registers.A | Registers.X,  // TXA
            Registers.A | Registers.Y,  // TAY
            Registers.A | Registers.Y,  // TYA
            Registers.S | Registers.X,  // TSX
            Registers.S | Registers.X,  // TXS
            Registers.A,                // PHA
            Registers.A,                // PLA
            Registers.P,                // PHP
            Registers.P,                // PLP
            Registers.None,             // JMP
            Registers.None,             // JSR
            Registers.None,             // RTS
            Registers.None,             // RTI
            Registers.P,                // SEC
            Registers.P,                // SED
            Registers.P,                // SEI
            Registers.P,                // CLC
            Registers.P,                // CLD
            Registers.P,                // CLI
            Registers.P,                // CLV
            Registers.None,             // NOP
            Registers.None,             // BRK
            Registers.None,             // Error
        };

        private static Registers[] sModeRegs =
        {
            Registers.None,     // None
            Registers.A,        // A
            Registers.None,     // i
            Registers.None,     // I
            Registers.None,     // a
            Registers.None,     // aN
            Registers.None,     // zp
            Registers.None,     // r
            Registers.X,        // ax
            Registers.Y,        // ay
            Registers.X,        // zpx
            Registers.Y,        // zpy
            Registers.X,        // zpxN
            Registers.Y,        // zpyN
            Registers.X,        // zpNx
            Registers.Y         // zpNy
        };

        private static byte[] sModeLen = 
        {
            0,  // None
            1,  // A
            1,  // i
            2,  // I
            3,  // a
            3,  // aN
            2,  // zp
            2,  // r
            3,  // ax
            3,  // ay
            2,  // zpx
            2,  // zpy
            2,  // zpxN
            2,  // zpyN
            2,  // zpNx
            2   // zpNy
        };

        private static InstDesc[] sInstTable =
        {
            new InstDesc { Class = Class.BRK, Mode = Mode.i },          // 00
            new InstDesc { Class = Class.ORA, Mode = Mode.zpxN },       // 01
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 02
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 03
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 04
            new InstDesc { Class = Class.ORA, Mode = Mode.zp },         // 05
            new InstDesc { Class = Class.ASL, Mode = Mode.zp },         // 06
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 07
            new InstDesc { Class = Class.PHP, Mode = Mode.i },          // 08
            new InstDesc { Class = Class.ORA, Mode = Mode.I },          // 09
            new InstDesc { Class = Class.ASL, Mode = Mode.A },          // 0A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 0B
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 0C
            new InstDesc { Class = Class.ORA, Mode = Mode.a },          // 0D
            new InstDesc { Class = Class.ASL, Mode = Mode.a },          // 0E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 0F

            new InstDesc { Class = Class.BPL, Mode = Mode.r },          // 10
            new InstDesc { Class = Class.ORA, Mode = Mode.zpNy },       // 11
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 12
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 13
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 14
            new InstDesc { Class = Class.ORA, Mode = Mode.zpx },        // 15
            new InstDesc { Class = Class.ASL, Mode = Mode.zpx },        // 16
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 17
            new InstDesc { Class = Class.CLC, Mode = Mode.i },          // 18
            new InstDesc { Class = Class.ORA, Mode = Mode.ay },         // 19
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 1A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 1B
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 1C
            new InstDesc { Class = Class.ORA, Mode = Mode.ax },         // 1D
            new InstDesc { Class = Class.ASL, Mode = Mode.ax },         // 1E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 1F

            new InstDesc { Class = Class.JSR, Mode = Mode.a },          // 20
            new InstDesc { Class = Class.AND, Mode = Mode.zpxN },       // 21
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 22
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 23
            new InstDesc { Class = Class.BIT, Mode = Mode.zp },         // 24
            new InstDesc { Class = Class.AND, Mode = Mode.zp },         // 25
            new InstDesc { Class = Class.ROL, Mode = Mode.zp },         // 26
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 27
            new InstDesc { Class = Class.PLP, Mode = Mode.i },          // 28
            new InstDesc { Class = Class.AND, Mode = Mode.I },          // 29
            new InstDesc { Class = Class.ROL, Mode = Mode.A },          // 2A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 2B
            new InstDesc { Class = Class.BIT, Mode = Mode.a },          // 2C
            new InstDesc { Class = Class.AND, Mode = Mode.a },          // 2D
            new InstDesc { Class = Class.ROL, Mode = Mode.a },          // 2E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 2F

            new InstDesc { Class = Class.BMI, Mode = Mode.r },          // 30
            new InstDesc { Class = Class.AND, Mode = Mode.zpNy },       // 31
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 32
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 33
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 34
            new InstDesc { Class = Class.AND, Mode = Mode.zpx },        // 35
            new InstDesc { Class = Class.ROL, Mode = Mode.zpx },        // 36
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 37
            new InstDesc { Class = Class.SEC, Mode = Mode.i },          // 38
            new InstDesc { Class = Class.AND, Mode = Mode.ay },         // 39
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 3A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 3B
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 3C
            new InstDesc { Class = Class.AND, Mode = Mode.ax },         // 3D
            new InstDesc { Class = Class.ROL, Mode = Mode.ax },         // 3E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 3F

            new InstDesc { Class = Class.RTI, Mode = Mode.i },          // 40
            new InstDesc { Class = Class.EOR, Mode = Mode.zpxN },       // 41
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 42
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 43
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 44
            new InstDesc { Class = Class.EOR, Mode = Mode.zp },         // 45
            new InstDesc { Class = Class.LSR, Mode = Mode.zp },         // 46
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 47
            new InstDesc { Class = Class.PHA, Mode = Mode.i },          // 48
            new InstDesc { Class = Class.EOR, Mode = Mode.I },          // 49
            new InstDesc { Class = Class.LSR, Mode = Mode.A },          // 4A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 4B
            new InstDesc { Class = Class.JMP, Mode = Mode.a },          // 4C
            new InstDesc { Class = Class.EOR, Mode = Mode.a },          // 4D
            new InstDesc { Class = Class.LSR, Mode = Mode.a },          // 4E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 4F

            new InstDesc { Class = Class.BVC, Mode = Mode.r },          // 50
            new InstDesc { Class = Class.EOR, Mode = Mode.zpNy },       // 51
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 52
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 53
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 54
            new InstDesc { Class = Class.EOR, Mode = Mode.zpx },        // 55
            new InstDesc { Class = Class.LSR, Mode = Mode.zpx },        // 56
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 57
            new InstDesc { Class = Class.CLI, Mode = Mode.i },          // 58
            new InstDesc { Class = Class.EOR, Mode = Mode.ay },         // 59
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 5A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 5B
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 5C
            new InstDesc { Class = Class.EOR, Mode = Mode.ax },         // 5D
            new InstDesc { Class = Class.LSR, Mode = Mode.ax },         // 5E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 5F

            new InstDesc { Class = Class.RTS, Mode = Mode.i },          // 60
            new InstDesc { Class = Class.ADC, Mode = Mode.zpxN },       // 61
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 62
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 63
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 64
            new InstDesc { Class = Class.ADC, Mode = Mode.zp },         // 65
            new InstDesc { Class = Class.ROR, Mode = Mode.zp },         // 66
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 67
            new InstDesc { Class = Class.PLA, Mode = Mode.i },          // 68
            new InstDesc { Class = Class.ADC, Mode = Mode.I },          // 69
            new InstDesc { Class = Class.ROR, Mode = Mode.A },          // 6A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 6B
            new InstDesc { Class = Class.JMP, Mode = Mode.aN },         // 6C
            new InstDesc { Class = Class.ADC, Mode = Mode.a },          // 6D
            new InstDesc { Class = Class.ROR, Mode = Mode.a },          // 6E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 6F

            new InstDesc { Class = Class.BVS, Mode = Mode.r },          // 70
            new InstDesc { Class = Class.ADC, Mode = Mode.zpNy },       // 71
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 72
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 73
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 74
            new InstDesc { Class = Class.ADC, Mode = Mode.zpx },        // 75
            new InstDesc { Class = Class.ROR, Mode = Mode.zpx },        // 76
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 77
            new InstDesc { Class = Class.SEI, Mode = Mode.i },          // 78
            new InstDesc { Class = Class.ADC, Mode = Mode.ay },         // 79
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 7A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 7B
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 7C
            new InstDesc { Class = Class.ADC, Mode = Mode.ax },         // 7D
            new InstDesc { Class = Class.ROR, Mode = Mode.ax },         // 7E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 7F

            new InstDesc { Class = Class.None, Mode = Mode.None },      // 80
            new InstDesc { Class = Class.STA, Mode = Mode.zpxN },       // 81
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 82
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 83
            new InstDesc { Class = Class.STY, Mode = Mode.zp },         // 84
            new InstDesc { Class = Class.STA, Mode = Mode.zp },         // 85
            new InstDesc { Class = Class.STX, Mode = Mode.zp },         // 86
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 87
            new InstDesc { Class = Class.DEY, Mode = Mode.i },          // 88
            new InstDesc { Class = Class.BIT, Mode = Mode.I },          // 89
            new InstDesc { Class = Class.TXA, Mode = Mode.i },          // 8A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 8B
            new InstDesc { Class = Class.STY, Mode = Mode.a },          // 8C
            new InstDesc { Class = Class.STA, Mode = Mode.a },          // 8D
            new InstDesc { Class = Class.STX, Mode = Mode.a },          // 8E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 8F

            new InstDesc { Class = Class.BCC, Mode = Mode.r },          // 90
            new InstDesc { Class = Class.STA, Mode = Mode.zpNy },       // 91
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 92
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 93
            new InstDesc { Class = Class.STY, Mode = Mode.zpx },        // 94
            new InstDesc { Class = Class.STA, Mode = Mode.zpx },        // 95
            new InstDesc { Class = Class.STX, Mode = Mode.zpy },        // 96
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 97
            new InstDesc { Class = Class.TYA, Mode = Mode.i },          // 98
            new InstDesc { Class = Class.STA, Mode = Mode.ay },         // 99
            new InstDesc { Class = Class.TXS, Mode = Mode.i },          // 9A
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 9B
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 9C
            new InstDesc { Class = Class.STA, Mode = Mode.ax },         // 9D
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 9E
            new InstDesc { Class = Class.None, Mode = Mode.None },      // 9F

            new InstDesc { Class = Class.LDY, Mode = Mode.I },          // A0
            new InstDesc { Class = Class.LDA, Mode = Mode.zpxN },       // A1
            new InstDesc { Class = Class.LDX, Mode = Mode.I },          // A2
            new InstDesc { Class = Class.None, Mode = Mode.None },      // A3
            new InstDesc { Class = Class.LDY, Mode = Mode.zp },         // A4
            new InstDesc { Class = Class.LDA, Mode = Mode.zp },         // A5
            new InstDesc { Class = Class.LDX, Mode = Mode.zp },         // A6
            new InstDesc { Class = Class.None, Mode = Mode.None },      // A7
            new InstDesc { Class = Class.TAY, Mode = Mode.i },          // A8
            new InstDesc { Class = Class.LDA, Mode = Mode.I },          // A9
            new InstDesc { Class = Class.TAX, Mode = Mode.i },          // AA
            new InstDesc { Class = Class.None, Mode = Mode.None },      // AB
            new InstDesc { Class = Class.LDY, Mode = Mode.a },          // AC
            new InstDesc { Class = Class.LDA, Mode = Mode.a },          // AD
            new InstDesc { Class = Class.LDX, Mode = Mode.a },          // AE
            new InstDesc { Class = Class.None, Mode = Mode.None },      // AF

            new InstDesc { Class = Class.BCS, Mode = Mode.r },          // B0
            new InstDesc { Class = Class.LDA, Mode = Mode.zpNy },       // B1
            new InstDesc { Class = Class.None, Mode = Mode.None },      // B2
            new InstDesc { Class = Class.None, Mode = Mode.None },      // B3
            new InstDesc { Class = Class.LDY, Mode = Mode.zpx },        // B4
            new InstDesc { Class = Class.LDA, Mode = Mode.zpx },        // B5
            new InstDesc { Class = Class.LDX, Mode = Mode.zpy },        // B6
            new InstDesc { Class = Class.None, Mode = Mode.None },      // B7
            new InstDesc { Class = Class.CLV, Mode = Mode.i },          // B8
            new InstDesc { Class = Class.LDA, Mode = Mode.ay },         // B9
            new InstDesc { Class = Class.TSX, Mode = Mode.i },          // BA
            new InstDesc { Class = Class.None, Mode = Mode.None },      // BB
            new InstDesc { Class = Class.LDY, Mode = Mode.ax },         // BC
            new InstDesc { Class = Class.LDA, Mode = Mode.ax },         // BD
            new InstDesc { Class = Class.LDX, Mode = Mode.ay },         // BE
            new InstDesc { Class = Class.None, Mode = Mode.None },      // BF

            new InstDesc { Class = Class.CPY, Mode = Mode.I },          // C0
            new InstDesc { Class = Class.CMP, Mode = Mode.zpxN },       // C1
            new InstDesc { Class = Class.None, Mode = Mode.None },      // C2
            new InstDesc { Class = Class.None, Mode = Mode.None },      // C3
            new InstDesc { Class = Class.CPY, Mode = Mode.zp },         // C4
            new InstDesc { Class = Class.CMP, Mode = Mode.zp },         // C5
            new InstDesc { Class = Class.DEC, Mode = Mode.zp },         // C6
            new InstDesc { Class = Class.None, Mode = Mode.None },      // C7
            new InstDesc { Class = Class.INY, Mode = Mode.i },          // C8
            new InstDesc { Class = Class.CMP, Mode = Mode.I },          // C9
            new InstDesc { Class = Class.DEX, Mode = Mode.i },          // CA
            new InstDesc { Class = Class.None, Mode = Mode.None },      // CB
            new InstDesc { Class = Class.CPY, Mode = Mode.a },          // CC
            new InstDesc { Class = Class.CMP, Mode = Mode.a },          // CD
            new InstDesc { Class = Class.DEC, Mode = Mode.a },          // CE
            new InstDesc { Class = Class.None, Mode = Mode.None },      // CF

            new InstDesc { Class = Class.BNE, Mode = Mode.r },          // D0
            new InstDesc { Class = Class.CMP, Mode = Mode.zpNy },       // D1
            new InstDesc { Class = Class.None, Mode = Mode.None },      // D2
            new InstDesc { Class = Class.None, Mode = Mode.None },      // D3
            new InstDesc { Class = Class.None, Mode = Mode.None },      // D4
            new InstDesc { Class = Class.CMP, Mode = Mode.zpx },        // D5
            new InstDesc { Class = Class.DEC, Mode = Mode.zpx },        // D6
            new InstDesc { Class = Class.None, Mode = Mode.None },      // D7
            new InstDesc { Class = Class.CLD, Mode = Mode.i },          // D8
            new InstDesc { Class = Class.CMP, Mode = Mode.ay },         // D9
            new InstDesc { Class = Class.None, Mode = Mode.None },      // DA
            new InstDesc { Class = Class.None, Mode = Mode.None },      // DB
            new InstDesc { Class = Class.None, Mode = Mode.None },      // DC
            new InstDesc { Class = Class.CMP, Mode = Mode.ax },         // DD
            new InstDesc { Class = Class.DEC, Mode = Mode.ax },         // DE
            new InstDesc { Class = Class.None, Mode = Mode.None },      // DF

            new InstDesc { Class = Class.CPX, Mode = Mode.I },          // E0
            new InstDesc { Class = Class.SBC, Mode = Mode.zpxN },       // E1
            new InstDesc { Class = Class.None, Mode = Mode.None },      // E2
            new InstDesc { Class = Class.None, Mode = Mode.None },      // E3
            new InstDesc { Class = Class.CPX, Mode = Mode.zp },         // E4
            new InstDesc { Class = Class.SBC, Mode = Mode.zp },         // E5
            new InstDesc { Class = Class.INC, Mode = Mode.zp },         // E6
            new InstDesc { Class = Class.None, Mode = Mode.None },      // E7
            new InstDesc { Class = Class.INX, Mode = Mode.i },          // E8
            new InstDesc { Class = Class.SBC, Mode = Mode.I },          // E9
            new InstDesc { Class = Class.NOP, Mode = Mode.i },          // EA
            new InstDesc { Class = Class.None, Mode = Mode.None },      // EB
            new InstDesc { Class = Class.CPX, Mode = Mode.zpx },        // EC
            new InstDesc { Class = Class.SBC, Mode = Mode.a },          // ED
            new InstDesc { Class = Class.INC, Mode = Mode.a },          // EE
            new InstDesc { Class = Class.None, Mode = Mode.None },      // EF

            new InstDesc { Class = Class.BEQ, Mode = Mode.r },          // F0
            new InstDesc { Class = Class.SBC, Mode = Mode.zpNy },       // F1
            new InstDesc { Class = Class.None, Mode = Mode.None },      // F2
            new InstDesc { Class = Class.None, Mode = Mode.None },      // F3
            new InstDesc { Class = Class.None, Mode = Mode.None },      // F4
            new InstDesc { Class = Class.SBC, Mode = Mode.zpx },        // F5
            new InstDesc { Class = Class.INC, Mode = Mode.zpx },        // F6
            new InstDesc { Class = Class.None, Mode = Mode.None },      // F7
            new InstDesc { Class = Class.SED, Mode = Mode.i },          // F8
            new InstDesc { Class = Class.SBC, Mode = Mode.ay },         // F9
            new InstDesc { Class = Class.None, Mode = Mode.None },      // FA
            new InstDesc { Class = Class.None, Mode = Mode.None },      // FB
            new InstDesc { Class = Class.None, Mode = Mode.None },      // FC
            new InstDesc { Class = Class.SBC, Mode = Mode.ax },         // FD
            new InstDesc { Class = Class.INC, Mode = Mode.ax },         // FE
            new InstDesc { Class = Class.None, Mode = Mode.None },      // FF
        };

        #endregion
    }
}
