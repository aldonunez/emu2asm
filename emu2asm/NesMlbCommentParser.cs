using System;

namespace emu2asm.NesMlb
{
    internal struct CommentParser
    {
        private enum Section
        {
            None,
            AboveLabel,
            BelowLabel,
            NextToInstruction,
            Attribute,
        }

        private string _comment;
        private int _index;
        private Section _section;

        private int _partAboveStart;
        private int _partAboveEnd;
        private int _partBelowStart;
        private int _partBelowEnd;
        private int _partSideStart;
        private int _partSideEnd;
        private object _attribute;

        public CommentParser( string comment )
        {
            _comment = comment;

            _index = 0;
            _section = Section.AboveLabel;
            _attribute = null;

            _partAboveStart = 0;
            _partAboveEnd = -1;
            _partBelowStart = -1;
            _partBelowEnd = -1;
            _partSideStart = -1;
            _partSideEnd = -1;
        }

        public object ParseAttribute()
        {
            Parse();

            return _attribute;
        }

        public CommentParts ParseAll()
        {
            Parse();

            var parts = new CommentParts();

            if ( _partAboveEnd >= 0 )
                parts.Above = _comment.Substring( _partAboveStart, _partAboveEnd - _partAboveStart );

            if ( _partBelowEnd >= 0 )
                parts.Below = _comment.Substring( _partBelowStart, _partBelowEnd - _partBelowStart );

            if ( _partSideEnd >= 0 )
                parts.Side = _comment.Substring( _partSideStart, _partSideEnd - _partSideStart );

            parts.Attribute = _attribute;

            return parts;
        }

        private void Parse()
        {
            while ( _index < _comment.Length )
            {
                ParseLine();
            }
        }

        private void ParseLine()
        {
            int lineStart = _index;
            int lineEnd = ReadLine();

            switch ( _section )
            {
                case Section.AboveLabel:
                    _partAboveEnd = lineEnd;
                    break;

                case Section.BelowLabel:
                    _partBelowEnd = lineEnd;
                    break;

                case Section.NextToInstruction:
                    _partSideEnd = lineEnd;
                    _section = Section.None;
                    break;

                case Section.Attribute:
                    _section = Section.None;
                    break;

                default:
                    if ( !IsSubstringWhitespace( lineStart, lineEnd ) )
                        throw new ApplicationException();
                    break;
            }
        }

        private int ReadLine()
        {
            SkipSpaces();
            if ( _index == _comment.Length )
                return _index;

            return ReadAttributeOrRestOfLine();
        }

        private int ReadRestOfLine()
        {
            for ( ; _index < _comment.Length; _index++ )
            {
                if ( _comment[_index] == '\\'
                    && (_index + 1) < _comment.Length
                    && _comment[_index + 1] == 'n' )
                {
                    int lineEnd = _index;
                    _index += 2;
                    return lineEnd;
                }
            }

            return _index;
        }

        private int ReadAttributeOrRestOfLine()
        {
            int attrStart = _index;
            int attrEnd = ReadOptionalAttribute( _index );
            int lineEnd = ReadRestOfLine();

            if ( attrEnd < 0 )
                return lineEnd;

            var attrSpan = _comment.AsSpan( attrStart, attrEnd - attrStart );

            if ( attrSpan.Length < 6 || !attrSpan.StartsWith<char>( "DASM." ) )
                return lineEnd;

            var subattrSpan = attrSpan.Slice( 5, attrSpan.Length - 6 );

            if ( subattrSpan.Equals( "BELOW", StringComparison.Ordinal ) )
            {
                ProcessBelowAttribute( attrEnd, lineEnd );
            }
            else if ( subattrSpan.Equals( "SIDE", StringComparison.Ordinal ) )
            {
                ProcessSideAttribute( attrEnd, lineEnd );
            }
            else
            {
                if ( _attribute != null )
                    throw new ApplicationException( "The comment has more than one attribute." );

                ProcessAttributeObject( ref subattrSpan, attrEnd, lineEnd );
            }

            return lineEnd;
        }

        private void ProcessAttributeObject( ref ReadOnlySpan<char> subattrSpan, int attrEnd, int lineEnd )
        {
            object attribute;

            _section = Section.Attribute;

            if ( subattrSpan.Equals( "ATAB", StringComparison.Ordinal ) )
            {
                attribute = new Disassembler.AddrTableDataAttribute( _comment, attrEnd, lineEnd );
            }
            else if ( subattrSpan.Equals( "ATABL", StringComparison.Ordinal ) )
            {
                attribute = new Disassembler.SplitAddrTableDataAttribute( true, _comment, attrEnd, lineEnd );
            }
            else if ( subattrSpan.Equals( "ATABH", StringComparison.Ordinal ) )
            {
                attribute = new Disassembler.SplitAddrTableDataAttribute( false, _comment, attrEnd, lineEnd );
            }
            else if ( subattrSpan.Equals( "HEAPDIR", StringComparison.Ordinal ) )
            {
                attribute = new Disassembler.HeapDirDataAttribute( _comment, attrEnd, lineEnd );
            }
            else if ( subattrSpan.Equals( "WORD", StringComparison.Ordinal ) )
            {
                attribute = new Disassembler.WordDataAttribute( _comment, attrEnd, lineEnd );
            }
            else if ( subattrSpan.Equals( "INCBIN", StringComparison.Ordinal ) )
            {
                attribute = new Disassembler.IncBinDataAttribute( _comment, attrEnd, lineEnd );
            }
            else if ( subattrSpan.Equals( "EXPR", StringComparison.Ordinal ) )
            {
                attribute = new Disassembler.ExprCodeAttribute( _comment, attrEnd, lineEnd );
            }
            else
            {
                string message = string.Format(
                    "Unrecognized disassembly attribute: DASM.{0}", subattrSpan.ToString() );
                throw new ApplicationException( message );
            }

            _attribute = attribute;
        }

        private int ReadOptionalAttribute( int start )
        {
            int i = start;

            for ( ; i < _comment.Length
                && (_comment[i] == '.' || char.IsLetterOrDigit( _comment[i] )); i++ )
            {
            }

            if ( i == _comment.Length || _comment[i] != ':' )
                return -1;

            return i + 1;
        }

        private void SkipSpaces()
        {
            while ( _index < _comment.Length
                && IsWhiteSpace( _comment[_index] ) )
            {
                _index++;
            }
        }

        private static bool IsWhiteSpace( char c )
        {
            return c == ' ';
        }

        private bool IsSubstringWhitespace( int start, int end )
        {
            for ( int i = start; i < end; i++ )
            {
                if ( !IsWhiteSpace( _comment[i] ) )
                    return false;
            }

            return true;
        }

        private void ProcessBelowAttribute( int attrEnd, int lineEnd )
        {
            if ( _section != Section.AboveLabel )
                throw new ApplicationException();

            bool allSpace = IsSubstringWhitespace( attrEnd, lineEnd );

            _section = Section.BelowLabel;
            _partBelowStart = allSpace ? _index : attrEnd;
        }

        private void ProcessSideAttribute( int attrEnd, int lineEnd )
        {
            if ( _section != Section.AboveLabel && _section != Section.BelowLabel )
                throw new ApplicationException();

            _section = Section.NextToInstruction;
            _partSideStart = attrEnd;
        }
    }


    internal struct CommentParts
    {
        public string Above;
        public string Below;
        public string Side;
        public object Attribute;
    }


    internal struct CommentAttributeParser
    {
        public enum TokenType
        {
            None,
            Number,
            String,
        }

        private string _stringDef;
        private int _index;
        private int _end;

        public TokenType Type { get; private set; }
        public int IntValue { get; private set; }
        public string StringValue { get; private set; }
        public int KeyStart { get; private set; }
        public int KeyEnd { get; private set; }

        public CommentAttributeParser( string def, int start, int end )
        {
            _stringDef = def;
            _index = start;
            _end = end;
            Type = TokenType.None;
            IntValue = 0;
            StringValue = null;
            KeyStart = 0;
            KeyEnd = 0;
        }

        public bool ParseField()
        {
            KeyStart = 0;
            KeyEnd = 0;

            if ( _index == _end )
                return false;

            if ( _stringDef[_index] == ',' )
                _index++;

            SkipSpaces();
            if ( _index == _end )
                return false;

            char c = _stringDef[_index];

            if ( char.IsLetter( c ) )
                ParseKeyAndAssignment();

            c = _stringDef[_index];

            if ( char.IsDigit( c ) )
                ParseDecNumber();
            else if ( c == '$' )
                ParseHexNumber();
            else if ( c == '"' )
                ParseString();
            else
                throw new FormatException();

            SkipSpaces();
            if ( _index == _end )
                return true;

            if ( _stringDef[_index] != ',' )
                throw new FormatException();

            return true;
        }

        private void ParseKeyAndAssignment()
        {
            KeyStart = _index;

            for ( ; _index < _end && char.IsLetterOrDigit( _stringDef[_index] ); _index++ )
            {
            }

            if ( _index == _end
                || (_stringDef[_index] != ' ' && _stringDef[_index] != '=') )
                throw new FormatException();

            KeyEnd = _index;

            SkipSpaces();
            if ( _index == _end
                || _stringDef[_index] != '=' )
                throw new FormatException();

            _index++;

            SkipSpaces();
            if ( _index == _end )
                throw new FormatException();
        }

        private void ParseDecNumber()
        {
            int tokenStart = _index;

            for ( ; _index < _end && char.IsDigit( _stringDef[_index] ); _index++ )
            {
            }

            if ( _index < _end
                && _stringDef[_index] != ' '
                && _stringDef[_index] != ',' )
                throw new FormatException();

            var span = _stringDef.AsSpan( tokenStart, _index - tokenStart );

            IntValue = int.Parse( span );
            Type = TokenType.Number;
        }

        private void ParseHexNumber()
        {
            // Skip the dollar sign.
            _index++;

            int tokenStart = _index;

            for ( ; _index < _end && IsHexDigit( _stringDef[_index] ); _index++ )
            {
            }

            if ( _index < _end
                && _stringDef[_index] != ' '
                && _stringDef[_index] != ',' )
                throw new FormatException();

            var span = _stringDef.AsSpan( tokenStart, _index - tokenStart );

            IntValue = int.Parse( span, System.Globalization.NumberStyles.HexNumber );
            Type = TokenType.Number;
        }

        private static bool IsHexDigit( char c )
        {
            return (c >= 'A' && c <= 'F')
                || (c >= 'a' && c <= 'f')
                || char.IsDigit( c );
        }

        private void ParseString()
        {
            // Skip the opening double quote.
            _index++;

            int tokenStart = _index;

            for ( ; _index < _end && _stringDef[_index] != '"'; _index++ )
            {
            }

            if ( _index == _end )
                throw new FormatException();

            int tokenEnd = _index;

            // Skip the closing double quote.
            _index++;

            StringValue = _stringDef.Substring( tokenStart, tokenEnd - tokenStart );
            Type = TokenType.String;
        }

        private void SkipSpaces()
        {
            while ( _index < _stringDef.Length
                && IsWhiteSpace( _stringDef[_index] ) )
            {
                _index++;
            }
        }

        private static bool IsWhiteSpace( char c )
        {
            return c == ' ';
        }

        public void ValidateFieldType( TokenType type )
        {
            if ( Type != type )
                throw new FormatException();
        }
    }
}
