﻿using System;
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
                new Option<bool>(
                    "--enable-cheap-labels",
                    "Turn auto-jump labels with a suffix into cheap labels" ),
                new Option<bool>(
                    "--enable-unnamed-labels",
                    "Turn auto-jump labels without a suffix into unnamed labels" ),
                new Option<bool>(
                    "--enable-embedded-refs",
                    "List offsets from labeled data blocks" ),
                new Option<bool>(
                    "--enable-addresses",
                    "Shows ROM offsets of code in comments" ),
            };

            disasmCmd.Handler = CommandHandler.Create<
                string, string, string, string,
                bool, bool, bool, bool, bool, bool
                >( Disassemble );

            var rootCmd = new RootCommand
            {
                disasmCmd
            };

            return rootCmd.Invoke( args );
        }

        private static void Disassemble(
            string config, string rom, string coverage, string labels,
            bool separateUnknown, bool enableComments, bool enableCheapLabels,
            bool enableUnnamedLabels, bool enableEmbeddedRefs, bool enableAddresses )
        {
            NesMlb.Processor.Disassemble(
                config, rom, coverage, labels,
                separateUnknown, enableComments, enableCheapLabels,
                enableUnnamedLabels, enableEmbeddedRefs, enableAddresses );
        }
    }
}
