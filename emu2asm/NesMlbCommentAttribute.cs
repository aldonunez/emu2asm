using System;
using System.IO;

namespace emu2asm.NesMlb
{
    internal abstract class DataAttribute
    {
        public abstract void ProcessBlock(
            Disassembler disasm, Segment segment, int offset, LabelRecord label );

        public abstract bool WriteBlock(
            Disassembler disasm,
            Segment segment, int offset, LabelRecord label,
            StreamWriter writer );

        // TODO: Consider an API to process for imports and exports.
    }


    partial class Disassembler
    {
        internal class AddrTableDataAttribute : DataAttribute
        {
            private int _mappedBank = -1;

            public AddrTableDataAttribute( string def, int attrEnd, int lineEnd )
            {
                var parser = new CommentAttributeParser( def, attrEnd, lineEnd );

                if ( parser.ParseField() )
                {
                    parser.ValidateFieldType( CommentAttributeParser.TokenType.Number );

                    _mappedBank = parser.IntValue;
                }
            }

            public override void ProcessBlock(
                Disassembler disasm, Segment segment, int offset, LabelRecord label )
            {
                if ( label.Length < 2 || (label.Length % 2) != 0 )
                    throw new Exception();

                byte[] tracedCoverage = disasm._tracedCoverage;

                if ( _mappedBank >= 0 )
                {
                    byte fillValue = (byte) (_mappedBank | Disassembler.TracedBankKnownFlag);

                    Array.Fill<byte>( tracedCoverage, fillValue, offset, label.Length );
                }

                tracedCoverage[offset] |= 0x40;
            }

            public override bool WriteBlock(
                Disassembler disasm,
                Segment segment, int offset, LabelRecord label,
                StreamWriter writer )
            {
                return false;
            }
        }


        internal abstract class SplitAddrTableDataAttribute : DataAttribute
        {
            private bool _isLow;
            private int _stride = -1;

            public SplitAddrTableDataAttribute( bool isLow, string def, int attrEnd, int lineEnd )
            {
                _isLow = isLow;

                var parser = new CommentAttributeParser( def, attrEnd, lineEnd );

                if ( parser.ParseField() )
                {
                    var keySpan = def.AsSpan( parser.KeyStart, parser.KeyEnd - parser.KeyStart );

                    if ( keySpan.Equals( "stride", StringComparison.Ordinal ) )
                    {
                        parser.ValidateFieldType( CommentAttributeParser.TokenType.Number );

                        _stride = parser.IntValue;
                    }
                }
            }

            public override void ProcessBlock(
                Disassembler disasm, Segment segment, int offset, LabelRecord label )
            {
                if ( label.Length < 1 )
                    throw new Exception();

                // The label passed in is for one half. Look up the label for the other half.

                int otherOffset = _isLow ? offset + label.Length : offset - label.Length;
                int otherNsOffset = segment.GetNamespaceOffset( otherOffset );
                LabelRecord otherLabel;

                if ( !segment.Namespace.ByAddress.TryGetValue( otherNsOffset, out otherLabel ) )
                {
                    string message = string.Format(
                        "No matching address table label was found for {0}", label.Name );
                    throw new Exception( message );
                }

                if ( otherLabel.Length != label.Length )
                {
                    string message = string.Format(
                        "Address table {0} does not match {1} in length", otherLabel.Name, label.Name );
                    throw new Exception( message );
                }
            }

            public override bool WriteBlock( Disassembler disasm, Segment segment, int offset, LabelRecord label, StreamWriter writer )
            {
                byte[] image = disasm._rom.Image;
                string prefix = _isLow ? "LO" : "HI";
                int distance = _isLow ? label.Length : -label.Length;
                string sFirstExpr = null;
                int entryOffset = 0;

                if ( _stride >= 0 )
                    sFirstExpr = GetEntryExpression( disasm, segment, offset, image, distance );

                for ( int i = 0; i < label.Length; i++, offset++ )
                {
                    if ( _stride >= 0 )
                    {
                        writer.WriteLine( "    .{0}BYTES {1}+{2}", prefix, sFirstExpr, entryOffset );
                        entryOffset += _stride;
                    }
                    else
                    {
                        string sExpr = GetEntryExpression( disasm, segment, offset, image, distance );
                        writer.WriteLine( "    .{0}BYTES {1}", prefix, sExpr );
                    }
                }

                writer.WriteLine();

                return true;
            }

            private string GetEntryExpression( Disassembler disasm, Segment segment, int offset, byte[] image, int distance )
            {
                int bA = image[offset];
                int bB = image[offset + distance];
                int loByte = _isLow ? bA : bB;
                int hiByte = _isLow ? bB : bA;
                ushort addr = (ushort) (loByte | (hiByte << 8));

                var entryLabel = disasm.FindAbsoluteAddressLabel( segment, addr, offset );
                string sExpr;

                if ( entryLabel != null && !string.IsNullOrEmpty( entryLabel.Name ) )
                    sExpr = string.Format( "{0}", entryLabel.Name );
                else
                    sExpr = string.Format( "${0:X4}", addr );
                return sExpr;
            }
        }


        internal class SplitAddrTableLoDataAttribute : SplitAddrTableDataAttribute
        {
            public SplitAddrTableLoDataAttribute( string def, int attrEnd, int lineEnd ) :
                base( true, def, attrEnd, lineEnd )
            {
            }
        }


        internal class SplitAddrTableHiDataAttribute : SplitAddrTableDataAttribute
        {
            public SplitAddrTableHiDataAttribute( string def, int attrEnd, int lineEnd ) :
                base( false, def, attrEnd, lineEnd )
            {
            }
        }


        internal class HeapDirDataAttribute : DataAttribute
        {
            public HeapDirDataAttribute( string comment, int attrEnd, int lineEnd )
            {
            }

            public override void ProcessBlock( Disassembler disasm, Segment segment, int offset, LabelRecord label )
            {
            }

            public override bool WriteBlock( Disassembler disasm, Segment segment, int offset, LabelRecord label, StreamWriter writer )
            {
                byte[] image = disasm._rom.Image;
                ushort headAddr = (ushort) (image[offset] | (image[offset + 1] << 8));

                var headLabel = disasm.FindAbsoluteAddressLabel( segment, headAddr, offset );

                if ( headLabel == null || string.IsNullOrEmpty( headLabel.Name ) )
                    return false;

                for ( int i = 0; i < label.Length; i += 2, offset += 2 )
                {
                    ushort addr = (ushort) (image[offset] | (image[offset + 1] << 8));
                    int diff = addr - headAddr;

                    writer.WriteLine( "    .ADDR {0}+{1}", headLabel.Name, diff );
                }

                writer.WriteLine();

                return true;
            }
        }


        internal class WordDataAttribute : DataAttribute
        {
            private bool _isBigEndian;

            public WordDataAttribute( string def, int attrEnd, int lineEnd )
            {
                var parser = new CommentAttributeParser( def, attrEnd, lineEnd );

                if ( parser.ParseField() )
                {
                    var keySpan = def.AsSpan( parser.KeyStart, parser.KeyEnd - parser.KeyStart );

                    if ( keySpan.Equals( "bigEndian", StringComparison.Ordinal ) )
                    {
                        parser.ValidateFieldType( CommentAttributeParser.TokenType.Number );

                        _isBigEndian = parser.IntValue != 0;
                    }
                }
            }

            public override void ProcessBlock( Disassembler disasm, Segment segment, int offset, LabelRecord label )
            {
            }

            public override bool WriteBlock( Disassembler disasm, Segment segment, int offset, LabelRecord label, StreamWriter writer )
            {
                byte[] image = disasm._rom.Image;

                for ( int i = 0; i < label.Length; i += 2, offset += 2 )
                {
                    byte a = image[offset];
                    byte b = image[offset + 1];

                    if ( _isBigEndian )
                    {
                        writer.WriteLine( "    .DBYT ${0:X2}{1:X2}", a, b );
                    }
                    else
                    {
                        writer.WriteLine( "    .WORD ${0:X2}{1:X2}", b, a );
                    }
                }

                writer.WriteLine();

                return true;
            }
        }


        internal class IncBinDataAttribute : DataAttribute
        {
            private string _filename;

            public IncBinDataAttribute( string def, int attrEnd, int lineEnd )
            {
                var parser = new CommentAttributeParser( def, attrEnd, lineEnd );

                if ( parser.ParseField() )
                {
                    var keySpan = def.AsSpan( parser.KeyStart, parser.KeyEnd - parser.KeyStart );

                    if ( keySpan.Equals( "file", StringComparison.Ordinal ) )
                    {
                        parser.ValidateFieldType( CommentAttributeParser.TokenType.String );

                        _filename = parser.StringValue;
                    }
                }
            }

            public override void ProcessBlock( Disassembler disasm, Segment segment, int offset, LabelRecord label )
            {
            }

            public override bool WriteBlock( Disassembler disasm, Segment segment, int offset, LabelRecord label, StreamWriter writer )
            {
                string fullDir = Path.GetDirectoryName( _filename );
                Directory.CreateDirectory( fullDir );

                using var stream = File.Open( _filename, FileMode.Create );

                stream.Write( disasm._rom.Image, offset, label.Length );

                writer.WriteLine( ".INCBIN \"{0}\"", _filename );
                writer.WriteLine();

                return true;
            }
        }


        internal class ExprCodeAttribute
        {
            public string Expression { get; }

            public ExprCodeAttribute( string def, int attrEnd, int lineEnd )
            {
                Expression = def.Substring( attrEnd, lineEnd - attrEnd ).Trim();
            }
        }
    }
}
