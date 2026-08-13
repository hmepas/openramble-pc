using System.Globalization;

namespace AsrBench;

/// <summary>
/// Convolution of numerals to numbers - Russian and English.
///
/// It is necessary that the quality assessment does not lie. The reference is written in words
/// ("сто двадцать восемь мегабайт"), and the model can return numbers ("128 мегабайт") - or
/// vice versa. Without convolution, such a match would be counted as two errors, and the WER
/// number would cease to mean anything.
///
/// The convolution is into <b>numbers</b> and not into words on purpose: Russian declines the
/// numeral and changes its gender ("двадцать пятое", "двадцать пятого", "двадцать пять"), so
/// the only stable form is a number.
///
/// What the convolution does not do: it does not guess unfamiliar forms. Everything not found
/// in the table of forms stays a word and is honestly counted as an error when compared.
/// </summary>
internal static class NumeralFolder
{
    /// <summary>What a single word inside a numeric run means.</summary>
    private abstract record Piece
    {
        /// <summary>An ordinary addend: "двадцать" → 20.</summary>
        public sealed record Value(double Amount) : Piece;

        /// <summary>A place multiplier: "тысяча" → ×1000 for everything accumulated on the left.</summary>
        public sealed record Scale(double Factor) : Piece;

        /// <summary>English <c>hundred</c>: multiplies what is on the left instead of adding to it.</summary>
        public sealed record Multiplier(double Factor) : Piece;

        /// <summary>The fractional half: "с половиной", "and a half".</summary>
        public sealed record Half : Piece;

        /// <summary>The decimal point: "два точка ноль один".</summary>
        public sealed record Point : Piece;

        /// <summary>A linking word inside a number: "and".</summary>
        public sealed record Filler : Piece;
    }

    /// <summary>
    /// Collapse every numeral in the sequence to digits.
    ///
    /// The input is already normalized: lower case, the Russian yo folded to ye, punctuation
    /// separated from words.
    /// </summary>
    internal static IReadOnlyList<string> Fold(IReadOnlyList<string> words)
    {
        IReadOnlyList<string> prepared = CollapseHalves(words);
        var result = new List<string>();
        var run = new List<Piece>();
        var runWords = new List<string>();

        void Flush()
        {
            int offset = 0;
            while (offset < run.Count)
            {
                (double Value, int Consumed)? combined = CombinePrefix(run.GetRange(offset, run.Count - offset));
                if (combined is not { Consumed: > 0 } prefix)
                {
                    // The piece did not form a number - return the word as it was.
                    result.Add(runWords[offset]);
                    offset += 1;
                    continue;
                }

                result.Add(Format(prefix.Value));
                offset += prefix.Consumed;
            }

            run.Clear();
            runWords.Clear();
        }

        foreach (string word in prepared)
        {
            Piece? piece = PieceFor(word, run);
            if (piece is not null)
            {
                run.Add(piece);
                runWords.Add(word);
                continue;
            }

            Flush();
            result.Add(word);
        }

        Flush();
        return result;
    }

    /// <summary>
    /// Fold "с половиной" and "and a half" into one word.
    ///
    /// Otherwise the preposition "с" would always have to be treated as a function word, and
    /// "три с утра" would lose the preposition.
    /// </summary>
    private static IReadOnlyList<string> CollapseHalves(IReadOnlyList<string> words)
    {
        var result = new List<string>();
        int index = 0;
        while (index < words.Count)
        {
            bool russian = StartsWith(words, index, ["с", "половиной"]);
            if (russian || StartsWith(words, index, ["and", "a", "half"]))
            {
                result.Add(HalfMarker);
                index += russian ? 2 : 3;
                continue;
            }

            result.Add(words[index]);
            index += 1;
        }

        return result;
    }

    private static bool StartsWith(IReadOnlyList<string> words, int index, string[] prefix)
    {
        if (index + prefix.Length > words.Count)
        {
            return false;
        }

        for (int offset = 0; offset < prefix.Length; offset++)
        {
            if (words[index + offset] != prefix[offset])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A separate character instead of a word: it cannot occur in the text.</summary>
    private const string HalfMarker = "\u0001половина";

    /// <summary>
    /// Digital notation: "128", "4.5", "4,5", "25-е", "10-я".
    ///
    /// Growing a case ending through a hyphen is the usual notation of an ordinal numeral. The
    /// model writes "10-я мысль" where "десятая мысль" was said; without parsing that form
    /// every such place would produce an extra error.
    /// </summary>
    private static double? DigitValue(string word)
    {
        if (word.Length == 0 || !char.IsDigit(word[0]))
        {
            return null;
        }

        string body = word.Replace(",", ".", StringComparison.Ordinal);
        int dash = body.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            string suffix = body[(dash + 1)..];
            if (suffix.Length == 0 || suffix.Length > 3 || !suffix.All(char.IsLetter))
            {
                return null;
            }

            body = body[..dash];
        }

        if (!body.All(character => char.IsDigit(character) || character == '.'))
        {
            return null;
        }

        return double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private static Piece? PieceFor(string word, IReadOnlyList<Piece> run)
    {
        if (DigitValue(word) is { } digits)
        {
            return new Piece.Value(digits);
        }

        if (Cardinals.TryGetValue(word, out double cardinal))
        {
            return new Piece.Value(cardinal);
        }

        if (Scales.TryGetValue(word, out double scale))
        {
            return new Piece.Scale(scale);
        }

        if (Multipliers.Contains(word))
        {
            return new Piece.Multiplier(100);
        }

        bool insideRun = run.Count > 0;
        if (word == HalfMarker)
        {
            return insideRun ? new Piece.Half() : null;
        }

        if (Points.Contains(word))
        {
            return insideRun ? new Piece.Point() : null;
        }

        // "and" glues digits only after hundreds or thousands: "three hundred and twenty one"
        // is a number, "two and three" is not.
        if (word == "and" && run.Count > 0 && IsScaleLike(run[^1]))
        {
            return new Piece.Filler();
        }

        return null;
    }

    private static bool IsScaleLike(Piece piece) => piece is Piece.Scale or Piece.Multiplier;

    /// <summary>
    /// Add up the longest beginning of the run into a number.
    ///
    /// Returns the number and how many words it ate. <c>null</c> - if there is no beginning of
    /// a number at all: for example "с" without "половиной" after it.
    ///
    /// What is read is given in pieces and not as a whole, because consecutive numerals are
    /// not always one number: "четырнадцать тридцать" is a time, two numbers, not forty-four.
    /// </summary>
    private static (double Value, int Consumed)? CombinePrefix(IReadOnlyList<Piece> pieces)
    {
        double total = 0;
        double current = 0;
        bool sawNumber = false;
        double fraction = 0;
        string? decimalDigits = null;
        double lastAdded = double.PositiveInfinity;
        int consumed = 0;

        // How many words are included in the finished number: trailing function words
        // ("точка" without digits, "and" without a continuation) are not counted in.
        int complete = 0;

        foreach (Piece piece in pieces)
        {
            if (piece is Piece.Value value)
            {
                if (decimalDigits is not null)
                {
                    // After the "point" the numerals are read as digits in a row:
                    // "два точка ноль один" → 2.01.
                    if (value.Amount != Math.Round(value.Amount, MidpointRounding.ToEven)
                        || value.Amount < 0
                        || value.Amount >= 100)
                    {
                        break;
                    }

                    decimalDigits += ((int)value.Amount).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    // The addends must decrease: "сто двадцать восемь" is one number, while
                    // "четырнадцать тридцать" is a time, that is two. Without this rule 14:30
                    // would turn into 44.
                    if (current != 0 && value.Amount >= lastAdded)
                    {
                        break;
                    }

                    current += value.Amount;
                    lastAdded = value.Amount;
                }

                sawNumber = true;
                consumed += 1;
                complete = consumed;
                continue;
            }

            if (piece is Piece.Scale scale)
            {
                if (decimalDigits is not null)
                {
                    break;
                }

                total += Math.Max(current, 1) * scale.Factor;
                current = 0;
                lastAdded = double.PositiveInfinity;
                sawNumber = true;
                consumed += 1;
                complete = consumed;
                continue;
            }

            if (piece is Piece.Multiplier multiplier)
            {
                if (decimalDigits is not null)
                {
                    break;
                }

                current = Math.Max(current, 1) * multiplier.Factor;
                lastAdded = double.PositiveInfinity;
                sawNumber = true;
                consumed += 1;
                complete = consumed;
                continue;
            }

            if (piece is Piece.Half)
            {
                if (!sawNumber || fraction != 0)
                {
                    break;
                }

                fraction = 0.5;
                consumed += 1;
                complete = consumed;
                continue;
            }

            if (piece is Piece.Point)
            {
                if (!sawNumber || decimalDigits is not null)
                {
                    break;
                }

                decimalDigits = string.Empty;
                consumed += 1;
                continue;
            }

            // Piece.Filler
            consumed += 1;
        }

        if (!sawNumber || complete == 0)
        {
            return null;
        }

        double result = total + current + fraction;
        if (decimalDigits is { Length: > 0 })
        {
            if (!double.TryParse(
                    "0." + decimalDigits,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double tail))
            {
                return null;
            }

            result += tail;
        }

        return (result, complete);
    }

    /// <remarks>
    /// Swift falls back to <c>String(format: "%g", value)</c>, which prints up to six
    /// significant digits and drops trailing zeros. <c>G6</c> is the same shape; the invariant
    /// culture keeps the decimal point a point on any machine.
    /// </remarks>
    private static string Format(double value)
    {
        if (value == Math.Round(value, MidpointRounding.ToEven) && Math.Abs(value) < 1e15)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("G6", CultureInfo.InvariantCulture);
    }

    // MARK: - Forms

    /// <summary>
    /// Addends. The forms are listed explicitly rather than derived from the stem: "сто" and
    /// "стол" differ by one letter, and guessing would turn the furniture into a number.
    /// </summary>
    private static readonly Dictionary<string, double> Cardinals = BuildCardinals();

    private static Dictionary<string, double> BuildCardinals()
    {
        var table = new Dictionary<string, double>(StringComparer.Ordinal);
        void Put(double value, string forms)
        {
            foreach (string form in forms.Split(
                         (char[])[' ', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                table[form] = value;
            }
        }

        Put(0, "ноль нуль нуля нулю нолем нулем нулевой нулевая нулевое нулевого zero");
        Put(1, """
            один одна одно одного одному одним одном одной одну
            первый первая первое первого первому первым первых первой первую
            one first
            """);
        Put(2, """
            два две двух двум двумя втором второй вторая второе второго второму вторых вторую
            two second
            """);
        Put(3, "три трех трем тремя третий третья третье третьего третьему третьих третью three third");
        Put(4, """
            четыре четырех четырем четырьмя
            четвертый четвертая четвертое четвертого четвертому четвертых четвертую
            four fourth
            """);
        Put(5, "пять пяти пятью пятый пятая пятое пятого пятому пятых пятую five fifth");
        Put(6, "шесть шести шестью шестой шестая шестое шестого шестому шестых шестую six sixth");
        Put(7, "семь семи семью седьмой седьмая седьмое седьмого седьмому седьмых седьмую seven seventh");
        Put(8, "восемь восьми восемью восьмью восьмой восьмая восьмое восьмого восьмых восьмую eight eighth");
        Put(9, "девять девяти девятью девятый девятая девятое девятого девятых девятую nine ninth");
        Put(10, "десять десяти десятью десятый десятая десятое десятого десятых десятую ten tenth");
        Put(11, "одиннадцать одиннадцати одиннадцатый одиннадцатая одиннадцатое одиннадцатого eleven eleventh");
        Put(12, "двенадцать двенадцати двенадцатый двенадцатая двенадцатое двенадцатого twelve twelfth");
        Put(13, "тринадцать тринадцати тринадцатый тринадцатая тринадцатое тринадцатого thirteen thirteenth");
        Put(14, """
            четырнадцать четырнадцати четырнадцатый четырнадцатая четырнадцатое четырнадцатого
            fourteen fourteenth
            """);
        Put(15, "пятнадцать пятнадцати пятнадцатый пятнадцатая пятнадцатое пятнадцатого fifteen fifteenth");
        Put(16, """
            шестнадцать шестнадцати шестнадцатый шестнадцатая шестнадцатое шестнадцатого
            sixteen sixteenth
            """);
        Put(17, "семнадцать семнадцати семнадцатый семнадцатая семнадцатое семнадцатого seventeen seventeenth");
        Put(18, """
            восемнадцать восемнадцати восемнадцатый восемнадцатая восемнадцатое восемнадцатого
            eighteen eighteenth
            """);
        Put(19, """
            девятнадцать девятнадцати девятнадцатый девятнадцатая девятнадцатое девятнадцатого
            nineteen nineteenth
            """);
        Put(20, "двадцать двадцати двадцатый двадцатая двадцатое двадцатого twenty twentieth");
        Put(30, "тридцать тридцати тридцатый тридцатая тридцатое тридцатого thirty thirtieth");
        Put(40, "сорок сорока сороковой сороковая сороковое сорокового forty fortieth");
        Put(50, "пятьдесят пятидесяти пятидесятый пятидесятая пятидесятое пятидесятого fifty fiftieth");
        Put(60, "шестьдесят шестидесяти шестидесятый шестидесятая шестидесятое шестидесятого sixty sixtieth");
        Put(70, "семьдесят семидесяти семидесятый семидесятая семидесятое семидесятого seventy seventieth");
        Put(80, """
            восемьдесят восьмидесяти восьмидесятый восьмидесятая восьмидесятое восьмидесятого
            eighty eightieth
            """);
        Put(90, "девяносто девяноста девяностый девяностая девяностое девяностого ninety ninetieth");
        Put(100, "сто ста сотый сотая сотое сотого");
        Put(200, "двести двухсот двумстам двухсотый двухсотая");
        Put(300, "триста трехсот трехсотый трехсотая");
        Put(400, "четыреста четырехсот четырехсотый");
        Put(500, "пятьсот пятисот пятисотый");
        Put(600, "шестьсот шестисот шестисотый");
        Put(700, "семьсот семисот семисотый");
        Put(800, "восемьсот восьмисот восьмисотый");
        Put(900, "девятьсот девятисот девятисотый");
        Put(1.5, "полтора полторы полутора");
        return table;
    }

    /// <summary>Places: multiply everything accumulated on the left and close the group.</summary>
    private static readonly Dictionary<string, double> Scales = BuildScales();

    private static Dictionary<string, double> BuildScales()
    {
        var table = new Dictionary<string, double>(StringComparer.Ordinal);
        void Put(double value, string forms)
        {
            foreach (string form in forms.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                table[form] = value;
            }
        }

        Put(1_000, "тысяча тысячи тысяч тысяче тысячу тысячей тысячный тысячная тысячного thousand thousandth");
        Put(1_000_000, "миллион миллиона миллионов миллиону миллионный миллионная million millionth");
        Put(1_000_000_000, "миллиард миллиарда миллиардов миллиардный billion billionth");
        return table;
    }

    /// <summary>English <c>hundred</c> is a multiplier, not an addend: <c>three hundred</c> = 300.</summary>
    private static readonly HashSet<string> Multipliers = new(StringComparer.Ordinal) { "hundred", "hundredth" };

    private static readonly HashSet<string> Points =
        new(StringComparer.Ordinal) { "точка", "запятая", "point" };
}
