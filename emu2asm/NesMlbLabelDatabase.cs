using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace emu2asm.NesMlb
{
    class LabelRecord
    {
        public string Name;
        public uint Address;
        public uint Length;
        public string Comment;
    }

    class LabelNamespace
    {
        public Dictionary<string, LabelRecord> ByName = new();
        public Dictionary<uint, LabelRecord> ByAddress = new();
    }

    class LabelDatabase
    {
        public LabelNamespace Program = new();
        public LabelNamespace Ram = new();
        public LabelNamespace SaveRam = new();
        public LabelNamespace WorkRam = new();
        public LabelNamespace Registers = new();

        public static LabelDatabase Make( TextReader textReader )
        {
            var db = new LabelDatabase();

            string line = textReader.ReadLine();

            while ( line != null )
            {
                string[] fields = line.Split( ':', 4 );
                char type;
                uint memLength = 1;
                uint memAddr;
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

                    uint memAddrEnd = uint.Parse( parts[1], NumberStyles.HexNumber );
                    memAddr = uint.Parse( parts[0], NumberStyles.HexNumber );
                    memLength = memAddrEnd - memAddr + 1;
                }
                else
                {
                    memAddr = uint.Parse( fields[1], NumberStyles.HexNumber );
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
                    case 'P': labelNamespace = db.Program; break;
                    case 'R': labelNamespace = db.Ram; break;
                    case 'S': labelNamespace = db.SaveRam; break;
                    case 'W': labelNamespace = db.WorkRam; break;
                    case 'G': labelNamespace = db.Registers; break;
                    default:
                        break;
                }

                if ( labelNamespace != null )
                {
                    labelNamespace.ByAddress.Add( record.Address, record );

                    if ( !string.IsNullOrEmpty( record.Name ) )
                        labelNamespace.ByName.Add( record.Name, record );
                }

                line = textReader.ReadLine();
            }

            return db;
        }
    }
}
