using System;

namespace emu2asm
{
    class Program
    {
        static void Main(string[] args)
        {
            NesMlb.Processor.Disassemble( args[0], args[1], args[2], args[3] );
        }
    }
}
