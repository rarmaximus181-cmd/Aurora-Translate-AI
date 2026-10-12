using System.Globalization;
using System.Text.RegularExpressions;

namespace AuroraTranslate.Services;

/// <summary>A bounded decimal arithmetic parser. It has no access to code execution or other system resources.</summary>
public static class CalculatorTool
{
    private static readonly Regex ArithmeticRequest = new(@"\d\s*[+*/-]\s*\d|\b(calculate|calculation|arithmetic|multiply|multiplication|divide|division|discount|sales tax|how much change|total cost|sum of|average of)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // Numerals route arithmetic in any selected language; the model decides whether calculation is actually needed.
    public static bool IsRelevantRequest(string text) => text.Any(char.IsDigit) || ArithmeticRequest.IsMatch(text);

    public static decimal Evaluate(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 512)
            throw new FormatException("Use a numeric expression between 1 and 512 characters.");
        try { return new Parser(expression).Parse(); }
        catch (OverflowException e) { throw new FormatException("The numeric value is too large to calculate safely.", e); }
        catch (DivideByZeroException e) { throw new FormatException("Division by zero is undefined.", e); }
    }

    public static string FormatValue(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private sealed class Parser(string expression)
    {
        private int _position;
        private int _operations;
        public decimal Parse()
        {
            var value = AddSubtract(0);
            SkipWhitespace();
            if (_position != expression.Length) throw new FormatException("Only decimal numbers, +, -, *, / and parentheses are supported.");
            return value;
        }

        private decimal AddSubtract(int depth)
        {
            var value = MultiplyDivide(depth);
            while (true)
            {
                SkipWhitespace();
                if (!Take('+') && !Take('-')) return value;
                var operation = expression[_position - 1];
                CountOperation();
                var next = MultiplyDivide(depth);
                value = operation == '+' ? value + next : value - next;
            }
        }

        private decimal MultiplyDivide(int depth)
        {
            var value = Factor(depth);
            while (true)
            {
                SkipWhitespace();
                if (!Take('*') && !Take('/')) return value;
                var operation = expression[_position - 1];
                CountOperation();
                var next = Factor(depth);
                value = operation == '*' ? value * next : value / next;
            }
        }

        private decimal Factor(int depth)
        {
            if (depth > 24) throw new FormatException("The expression has too many nested parentheses or signs.");
            SkipWhitespace();
            if (Take('+')) { CountOperation(); return Factor(depth + 1); }
            if (Take('-')) { CountOperation(); return -Factor(depth + 1); }
            if (Take('('))
            {
                var result = AddSubtract(depth + 1);
                SkipWhitespace();
                if (!Take(')')) throw new FormatException("The expression has an unmatched parenthesis.");
                return result;
            }
            var start = _position;
            while (_position < expression.Length && (char.IsAsciiDigit(expression[_position]) || expression[_position] == '.')) _position++;
            var number = expression[start.._position];
            var point = number.IndexOf('.');
            if (point >= 0)
            {
                var integerDigits = number[..point].TrimStart('0').Length;
                var fraction = number[(point + 1)..];
                if (fraction.Length > 28 || integerDigits + fraction.TrimEnd('0').Length > 29)
                    throw new FormatException("Use decimal numbers with at most 28 decimal places and 29 significant digits.");
            }
            if (start == _position || !decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                throw new FormatException("The expression contains an invalid decimal number.");
            return value;
        }

        private void CountOperation() { if (++_operations > 128) throw new FormatException("The expression contains too many operations."); }
        private void SkipWhitespace() { while (_position < expression.Length && char.IsWhiteSpace(expression[_position])) _position++; }
        private bool Take(char expected)
        {
            if (_position >= expression.Length || expression[_position] != expected) return false;
            _position++;
            return true;
        }
    }
}
