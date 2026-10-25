// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XEngine.OrigamiUI;

/// <summary>
/// Lightweight math expression parser for numeric fields, mirroring Unity's Inspector numeric
/// expressions. Evaluates arithmetic (+ - * / % ^), parentheses, unary minus, constants
/// (pi, tau, e), named functions (sin, cos, sqrt, min, max, ...), compound assignment against
/// the current value (+=, -=, *=, /=, %=), and multi-object functions L(a,b) (linear ramp) and
/// R(a,b) (random) which resolve per-target via an evaluation context.
/// </summary>
public static class MathParser
{
    /// <summary>
    /// Try to parse and evaluate a math expression. Returns true if successful.
    /// </summary>
    public static bool TryEvaluate(string expression, out double result)
        => TryEvaluate(expression, 0.0, 0, 1, out result);

    /// <summary>
    /// Try to parse and evaluate a math expression in context. Compound assignments
    /// (<c>+= 2</c>) resolve against <paramref name="current"/>; <c>L(a,b)</c> ramps across
    /// target <paramref name="index"/> of <paramref name="count"/>; <c>R(a,b)</c> picks a
    /// random value in [a,b). Returns true if successful.
    /// </summary>
    public static bool TryEvaluate(string expression, double current, int index, int count, out double result)
        => TryEvaluate(expression, current, index, count, null, out result);

    /// <summary>
    /// Try to parse and evaluate a math expression in context. Compound assignments
    /// (<c>+= 2</c>) resolve against <paramref name="current"/>; <c>L(a,b)</c> ramps across
    /// target <paramref name="index"/> of <paramref name="count"/>; <c>R(a,b)</c> picks a
    /// random value in [a,b) using <paramref name="rng"/> (or a shared instance when null).
    /// Returns true if successful.
    /// </summary>
    public static bool TryEvaluate(string expression, double current, int index, int count, Random? rng, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        // Compound assignment prefix: "+= 2*3" becomes "(current) + (2*3)".
        string expr = expression.Trim();
        if (expr.Length >= 2 && expr[1] == '=' && "+-*/%".IndexOf(expr[0]) >= 0 && expr[0] != '(')
        {
            string rest = expr[2..].Trim();
            if (rest.Length == 0) return false;
            expr = $"({current.ToString("G17", CultureInfo.InvariantCulture)}){expr[0]}({rest})";
        }

        try
        {
            var ctx = new EvalContext(index, count, rng ?? SharedRandom);
            result = Evaluate(expr, ctx);
            return !double.IsNaN(result) && !double.IsInfinity(result);
        }
        catch
        {
            return false;
        }
    }

    private static readonly Random SharedRandom = new();

    private readonly struct EvalContext
    {
        public readonly int Index;
        public readonly int Count;
        public readonly Random Rng;
        public EvalContext(int index, int count, Random rng) { Index = index; Count = count; Rng = rng; }
    }

    private static double Evaluate(string expression, in EvalContext ctx)
    {
        var clean = Regex.Replace(expression, @"\s+", "");
        var tokens = Tokenize(clean);
        var postfix = ShuntingYard(tokens);
        return EvaluatePostfix(postfix, ctx);
    }

    private static List<string> Tokenize(string expression)
    {
        var tokens = new List<string>();
        var matches = Regex.Matches(expression, @"(\+|-|\*|/|%|\^|\(|\)|,)|(\d+(\.\d+)?)|([a-zA-Z][a-zA-Z0-9]*)");

        for (int i = 0; i < matches.Count; i++)
        {
            string val = matches[i].Value;

            // Handle unary minus (negative numbers)
            if (val == "-" && (i == 0 || "^*/%(+-,".Contains(matches[i - 1].Value)))
            {
                if (i + 1 < matches.Count &&
                    double.TryParse("-" + matches[i + 1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    tokens.Add("-" + matches[++i].Value);
                }
                else
                {
                    tokens.Add("-1");
                    tokens.Add("*");
                }
            }
            else
            {
                tokens.Add(val);
            }
        }
        return tokens;
    }

    // Function calls tokenize as "fn:name" followed by their argument list. Arity is resolved at
    // ')' time from the comma count (so log works with one or two arguments) and carried in the
    // token as "fn:name:arity".
    private const string FnPrefix = "fn:";

    private static string FnToken(string name) => FnPrefix + name.ToLowerInvariant();

    /// <summary>Fixed arity per function; -1 = flexible (1 or 2), -3 = two or more, -2 = unknown name.</summary>
    private static int RequiredArity(string name) => name switch
    {
        "min" or "max" => -3,
        "pow" or "atan2" or "l" or "r" => 2,
        "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "abs" or "sign"
            or "floor" or "ceil" or "round" or "sqrt" or "exp" or "log2" or "log10" => 1,
        "log" => -1,
        _ => -2,
    };

    private static bool IsKnownFunction(string name) => RequiredArity(name.ToLowerInvariant()) != -2;

    private static List<string> ShuntingYard(List<string> tokens)
    {
        var output = new List<string>();
        var ops = new Stack<string>();
        // Argument count per open paren, so fn calls can resolve their arity at close.
        var argCounts = new Stack<int>();

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                output.Add(token);
            }
            else if (IsConstant(token))
            {
                output.Add(token);
            }
            else if (char.IsLetter(token[0]))
            {
                // A letter run is either a constant (pi/e/tau) or a function name; functions are
                // only recognized when a "(" follows, otherwise the postfix pass reports unknown.
                if (IsConstant(token))
                    output.Add(token);
                else if (i + 1 < tokens.Count && tokens[i + 1] == "(" && IsKnownFunction(token))
                    ops.Push(FnToken(token));
                else
                    output.Add(token); // unknown identifier - fails in EvaluatePostfix
            }
            else if (token == "(")
            {
                ops.Push(token);
                argCounts.Push(0);
            }
            else if (token == ",")
            {
                while (ops.Count > 0 && ops.Peek() != "(")
                    output.Add(ops.Pop());
                if (ops.Count == 0) throw new FormatException("Comma outside parentheses");
                if (argCounts.Count > 0)
                {
                    int c = argCounts.Pop();
                    argCounts.Push(c + 1);
                }
            }
            else if (token == ")")
            {
                while (ops.Count > 0 && ops.Peek() != "(")
                    output.Add(ops.Pop());
                if (ops.Count == 0) throw new FormatException("Mismatched parentheses");
                ops.Pop();
                int args = argCounts.Count > 0 ? argCounts.Pop() + 1 : 1;
                if (ops.Count > 0 && ops.Peek().StartsWith(FnPrefix, StringComparison.Ordinal))
                    output.Add(ops.Pop() + ":" + args);   // op already carries the "fn:" prefix
            }
            else
            {
                while (ops.Count > 0 && Precedence(token) <= Precedence(ops.Peek()))
                    output.Add(ops.Pop());
                ops.Push(token);
            }
        }

        while (ops.Count > 0)
        {
            if (ops.Peek() == "(") throw new FormatException("Mismatched parentheses");
            output.Add(ops.Pop());
        }
        return output;
    }

    private static double EvaluatePostfix(List<string> postfix, in EvalContext ctx)
    {
        var stack = new Stack<double>();

        foreach (string token in postfix)
        {
            if (token.StartsWith(FnPrefix, StringComparison.Ordinal))
            {
                // "fn:name:arity" (emitted by the shunting-yard pass).
                string body = token[FnPrefix.Length..];
                int lastColon = body.LastIndexOf(':');
                string name = lastColon >= 0 ? body[..lastColon] : body;
                int arity = lastColon >= 0 && int.TryParse(body[(lastColon + 1)..], out int explicitArity)
                    ? explicitArity
                    : Math.Max(1, RequiredArity(name));

                int required = RequiredArity(name);
                if (required == -2) throw new FormatException($"Unknown function: {name}");
                if (required >= 0 && arity != required) throw new FormatException($"Wrong argument count for {name}");
                if (required == -1 && arity is < 1 or > 2) throw new FormatException($"Wrong argument count for {name}");
                if (required == -3 && arity < 2) throw new FormatException($"Wrong argument count for {name}");
                if (stack.Count < arity) throw new FormatException("Invalid expression");

                // min/max fold across any higher arity (commutative, so pop order is irrelevant).
                if (required == -3 && arity > 2)
                {
                    double acc = stack.Pop();
                    for (int i = 1; i < arity; i++)
                    {
                        double v = stack.Pop();
                        acc = name == "min" ? Math.Min(acc, v) : Math.Max(acc, v);
                    }
                    stack.Push(acc);
                    continue;
                }

                // Two-arg functions pop (b, a) in push order: a comes first in the source text.
                double b = arity == 2 ? stack.Pop() : 0;
                double a = stack.Pop();
                stack.Push(ApplyFunction(name, a, b, ctx));
            }
            else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                stack.Push(number);
            }
            else if (TryGetConstant(token, out double constVal))
            {
                stack.Push(constVal);
            }
            else
            {
                if (stack.Count < 2) throw new FormatException("Invalid expression");
                double r = stack.Pop();
                double l = stack.Pop();
                stack.Push(ApplyOp(token, l, r));
            }
        }

        if (stack.Count != 1) throw new FormatException("Invalid expression");
        return stack.Pop();
    }

    private static int Precedence(string op) => op switch
    {
        "+" or "-" => 1,
        "*" or "/" or "%" => 2,
        "^" => 3,
        _ => 0,
    };

    private static double ApplyOp(string op, double l, double r) => op switch
    {
        "+" => l + r,
        "-" => l - r,
        "*" => l * r,
        "/" => r == 0 ? double.NaN : l / r,
        "%" => l % r,
        "^" => Math.Pow(l, r),
        _ => throw new FormatException($"Unknown operator: {op}"),
    };

    private static double ApplyFunction(string name, double a, double b, in EvalContext ctx) => name switch
    {
        "sin" => Math.Sin(a),
        "cos" => Math.Cos(a),
        "tan" => Math.Tan(a),
        "asin" => Math.Asin(a),
        "acos" => Math.Acos(a),
        "atan" => Math.Atan(a),
        "atan2" => Math.Atan2(a, b),
        "abs" => Math.Abs(a),
        "sign" => Math.Sign(a),
        "floor" => Math.Floor(a),
        "ceil" => Math.Ceiling(a),
        "round" => Math.Round(a, MidpointRounding.AwayFromZero),
        "min" => Math.Min(a, b),
        "max" => Math.Max(a, b),
        "sqrt" => Math.Sqrt(a),
        "pow" => Math.Pow(a, b),
        "exp" => Math.Exp(a),
        "log" => b == 0 ? Math.Log(a) : Math.Log(a, b),
        "log2" => Math.Log2(a),
        "log10" => Math.Log10(a),
        // L(a, b): linear ramp across the multi-object selection. A single target gets a.
        "l" => ctx.Count <= 1 ? a : a + (b - a) * (ctx.Index / (double)(ctx.Count - 1)),
        // R(a, b): random value in [a, b).
        "r" => a + ctx.Rng.NextDouble() * (b - a),
        _ => throw new FormatException($"Unknown function: {name}"),
    };

    private static bool IsConstant(string token) => token switch
    {
        "pi" or "PI" => true,
        "e" or "E" => true,
        "tau" or "TAU" => true,
        _ => false,
    };

    private static bool TryGetConstant(string token, out double value)
    {
        value = token switch
        {
            "pi" or "PI" => Math.PI,
            "e" or "E" => Math.E,
            "tau" or "TAU" => Math.Tau,
            _ => 0,
        };
        return IsConstant(token);
    }
}
