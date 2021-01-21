using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace emu2asm.NesMlb
{
    enum LabelType
    {
        Program,
        Ram,
        SaveRam,
        WorkRam,
        Registers,
    }

    enum LabelScope
    {
        Unknown,
        Full,
        Module,
        Cheap,
        Unnamed,
    }

    [DebuggerDisplay( "{Name}, {Address}" )]
    class LabelRecord
    {
        public LabelType Type;
        public string Name;
        public int Address;
        public int Length;
        public string Comment;

        public int SegmentId = -1;
        public string OperandExpr;

        public LabelScope Scope;
        public string CheapTag;
    }

    class LabelNamespace
    {
        public LabelType Type { get; }
        public Dictionary<string, LabelRecord> ByName = new();
        public Dictionary<int, LabelRecord> ByAddress = new();
        public SortedList<int, LabelRecord> Autojump = new();
        public List<LabelRecord> SortedNames = new();

        public LabelNamespace( LabelType type ) => Type = type;
    }

    class LabelDatabase
    {
        public LabelNamespace Program = new( LabelType.Program );
        public LabelNamespace Ram = new( LabelType.Ram );
        public LabelNamespace SaveRam = new( LabelType.SaveRam );
        public LabelNamespace WorkRam = new( LabelType.WorkRam );
        public LabelNamespace Registers = new( LabelType.Registers );

        public static Regex AutojumpLabelRegex = new Regex( "^L[0-9A-F]{2,}(?:_(.+))?$" );
        public static Regex PlainAutojumpRegex = new Regex( "^L[0-9A-F]{2,}$" );

        public static LabelDatabase Make( TextReader textReader )
        {
            var db = new LabelDatabase();

            string line = textReader.ReadLine();

            while ( line != null )
            {
                string[] fields = line.Split( ':', 4 );
                char type;
                int memLength = 1;
                int memAddr;
                string comment = null;

                if ( fields.Length < 3 )
                    throw new ApplicationException();
                else if ( fields.Length == 4 )
                    comment = fields[3];

                if ( fields[0].Length != 1 )
                    throw new ApplicationException();

                type = fields[0][0];

                if ( fields[1].Contains( '-' ) )
                {
                    string[] parts = fields[1].Split( '-', 2 );

                    int memAddrEnd = int.Parse( parts[1], NumberStyles.HexNumber );
                    memAddr = int.Parse( parts[0], NumberStyles.HexNumber );
                    memLength = memAddrEnd - memAddr + 1;
                }
                else
                {
                    memAddr = int.Parse( fields[1], NumberStyles.HexNumber );
                }

                var record = new LabelRecord
                {
                    Name = fields[2],
                    Address = memAddr,
                    Length = memLength,
                    Comment = comment
                };

                LabelNamespace labelNamespace = null;

                switch ( type )
                {
                    case 'P': labelNamespace = db.Program;   record.Type = LabelType.Program; break;
                    case 'R': labelNamespace = db.Ram;       record.Type = LabelType.Ram; break;
                    case 'S': labelNamespace = db.SaveRam;   record.Type = LabelType.SaveRam; break;
                    case 'W': labelNamespace = db.WorkRam;   record.Type = LabelType.WorkRam; break;
                    case 'G': labelNamespace = db.Registers; record.Type = LabelType.Registers; break;
                    default:
                        break;
                }

                if ( labelNamespace != null )
                {
                    labelNamespace.ByAddress.Add( record.Address, record );

                    if ( !string.IsNullOrEmpty( record.Name ) )
                    {
                        labelNamespace.ByName.Add( record.Name, record );
                        labelNamespace.SortedNames.Add( record );

                        if ( PlainAutojumpRegex.IsMatch( record.Name ) )
                            labelNamespace.Autojump.Add( record.Address, record );
                    }
                }

                line = textReader.ReadLine();
            }

            return db;
        }
    }
}
