using System;
using System.Text.RegularExpressions;

public class Test {
    private static string? ExtractMeetCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"[a-z0-9]{2,4}-[a-z0-9]{4,5}-[a-z0-9]{3}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }
    public static void Main(){
        var msg = "Conéctate a este Meet: Meet 2 · omp-srcb-uix";
        var code = ExtractMeetCode(msg);
        Console.WriteLine(code ?? "null");
    }
}
