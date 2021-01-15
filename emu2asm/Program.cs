using System;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace emu2asm
{
    class Program
    {
        static int Main(string[] args)
        {
            var disasmCmd = new Command( "disassemble" )
            {
                new Argument<string>(
                    "config",
                    "An XML file used to configure banks and metadata" ),
                new Argument<string>(
                    "rom",
                    "The .NES file to disassemble" ),
                new Argument<string>(
                    "coverage",
                    "A .CDL file describing code and data coverage" ),
                new Argument<string>(
                    "labels",
                    "A .MLB file containing labels and comments" ),
                new Option<bool>(
                    "--separate-unknown",
                    "Always split unknown data from known data" ),
                new Option<bool>(
                    "--enable-comments",
                    "Include comments in the disassembly" ),
            };

            disasmCmd.Handler = CommandHandler.Create<
                string, string, string, string,
                bool, bool
                >( Disassemble );

            var rootCmd = new RootCommand
            {
                disasmCmd
            };

            return rootCmd.Invoke( args );
        }

        private static void Disassemble(
            string config, string rom, string coverage, string labels,
            bool separateUnknown, bool enableComments )
        {
            NesMlb.Processor.Disassemble(
                config, rom, coverage, labels,
                separateUnknown, enableComments );
        }
    }
}
