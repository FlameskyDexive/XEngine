// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime.Platforms.Android;
using XEngine.Runtime.Platforms.HarmonyOS;
using XEngine.Runtime.UI;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class InputFieldContentTests
{
    [Fact]
    public void ContentTypesFilterCharactersAndSelectKeyboards()
    {
        Assert.Equal("12-3", InputFieldContent.Sanitize("12a-3!", InputFieldContentType.IntegerNumber));
        Assert.Equal("12.5", InputFieldContent.Sanitize("12.5x", InputFieldContentType.DecimalNumber));
        Assert.Equal("A1", InputFieldContent.Sanitize("A1!", InputFieldContentType.Alphanumeric));
        Assert.Equal("Ann-Marie", InputFieldContent.Sanitize("Ann-Marie!", InputFieldContentType.Name));
        Assert.Equal("a+b@c.de", InputFieldContent.Sanitize("a+b@c.de!", InputFieldContentType.EmailAddress));
        Assert.Equal("1234", InputFieldContent.Sanitize("12a34", InputFieldContentType.Pin));
        Assert.Equal("keep all", InputFieldContent.Sanitize("keep all", InputFieldContentType.Standard));

        Assert.Equal(TouchScreenKeyboardType.NumberPad, InputFieldContent.KeyboardType(InputFieldContentType.IntegerNumber));
        Assert.Equal(TouchScreenKeyboardType.DecimalPad, InputFieldContent.KeyboardType(InputFieldContentType.DecimalNumber));
        Assert.Equal(TouchScreenKeyboardType.EmailAddress, InputFieldContent.KeyboardType(InputFieldContentType.EmailAddress));
        Assert.True(InputFieldContent.IsSecure(InputFieldContentType.Password));
        Assert.True(InputFieldContent.IsSecure(InputFieldContentType.Pin));
        Assert.False(InputFieldContent.IsSecure(InputFieldContentType.Standard));
    }

    [Fact]
    public void PlatformHandlersRememberSoftKeyboardOptions()
    {
        var android = new AndroidInputHandler(softKeyboard: static _ => { });
        android.SetTextInputOptions(TouchScreenKeyboardType.EmailAddress, secure: true);
        Assert.Equal(TouchScreenKeyboardType.EmailAddress, android.LastKeyboardType);
        Assert.True(android.LastSecureTextEntry);

        var harmony = new HarmonyOSInputHandler(softKeyboard: static _ => { });
        harmony.SetTextInputOptions(TouchScreenKeyboardType.NumberPad, secure: false);
        Assert.Equal(TouchScreenKeyboardType.NumberPad, harmony.LastKeyboardType);
        Assert.False(harmony.LastSecureTextEntry);
    }

    [Fact]
    public void AndroidInputTypeMapsKeyboardAndSecureFlags()
    {
        int email = InputFieldContent.AndroidInputType(TouchScreenKeyboardType.EmailAddress, secure: false);
        Assert.Equal(InputFieldContent.AndroidClassText | InputFieldContent.AndroidTextVariationEmailAddress, email);

        int number = InputFieldContent.AndroidInputType(TouchScreenKeyboardType.NumberPad, secure: false);
        Assert.Equal(InputFieldContent.AndroidClassNumber | InputFieldContent.AndroidNumberFlagSigned, number);

        int pin = InputFieldContent.AndroidInputType(TouchScreenKeyboardType.NumberPad, secure: true);
        Assert.Equal(number | InputFieldContent.AndroidNumberVariationPassword, pin);

        int password = InputFieldContent.AndroidInputType(TouchScreenKeyboardType.Default, secure: true);
        Assert.Equal(InputFieldContent.AndroidClassText | InputFieldContent.AndroidTextVariationPassword | InputFieldContent.AndroidTextFlagNoSuggestions, password);

        var android = new AndroidInputHandler(softKeyboard: static _ => { });
        android.SetTextInputOptions(TouchScreenKeyboardType.DecimalPad, secure: false);
        Assert.Equal(InputFieldContent.AndroidInputType(TouchScreenKeyboardType.DecimalPad, false), android.LastAndroidInputType);

        int shown = 0;
        var restart = new AndroidInputHandler(softKeyboard: _ => shown++);
        restart.SetTextInputEnabled(true);
        restart.SetTextInputOptions(TouchScreenKeyboardType.EmailAddress, secure: false);
        Assert.Equal(2, shown);
        Assert.Equal(email, restart.LastAndroidInputType);
    }

    [Fact]
    public void HarmonyOSInputTypeMapsKeyboardAndSecureFlags()
    {
        int email = InputFieldContent.HarmonyOSInputType(TouchScreenKeyboardType.EmailAddress, secure: false);
        Assert.Equal(InputFieldContent.HarmonyOSEmailAddress, email);

        int number = InputFieldContent.HarmonyOSInputType(TouchScreenKeyboardType.NumberPad, secure: false);
        Assert.Equal(InputFieldContent.HarmonyOSNumber, number);

        int decimalPad = InputFieldContent.HarmonyOSInputType(TouchScreenKeyboardType.DecimalPad, secure: false);
        Assert.Equal(InputFieldContent.HarmonyOSNumberDecimal, decimalPad);

        int pin = InputFieldContent.HarmonyOSInputType(TouchScreenKeyboardType.NumberPad, secure: true);
        Assert.Equal(InputFieldContent.HarmonyOSNumberPassword, pin);

        int password = InputFieldContent.HarmonyOSInputType(TouchScreenKeyboardType.Default, secure: true);
        Assert.Equal(InputFieldContent.HarmonyOSNewPassword, password);

        var harmony = new HarmonyOSInputHandler(softKeyboard: static _ => { });
        harmony.SetTextInputOptions(TouchScreenKeyboardType.DecimalPad, secure: false);
        Assert.Equal(InputFieldContent.HarmonyOSInputType(TouchScreenKeyboardType.DecimalPad, false), harmony.LastHarmonyOSInputType);
        Assert.False(harmony.LastTextInputActive);

        int shown = 0;
        var restart = new HarmonyOSInputHandler(softKeyboard: _ => shown++);
        restart.SetTextInputEnabled(true);
        restart.SetTextInputOptions(TouchScreenKeyboardType.EmailAddress, secure: false);
        Assert.Equal(2, shown);
        Assert.True(restart.LastTextInputActive);
        Assert.Equal(email, restart.LastHarmonyOSInputType);
    }

    [Fact]
    public void HarmonyOSImeInputType_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "HARMONYOS IME INPUT TYPE", 230, 232, 240, 2);
        board.Text(40, 68, "CONTENT TYPE SELECTS NATIVE KEYBOARD", 140, 146, 160, 1);

        (string label, InputFieldContentType type)[] cases =
        [
            ("MAIL", InputFieldContentType.EmailAddress),
            ("INT", InputFieldContentType.IntegerNumber),
            ("PIN", InputFieldContentType.Pin),
            ("PASS", InputFieldContentType.Password),
        ];
        int[] xs = [48, 250, 452, 654];
        for (int i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            var keyboard = InputFieldContent.KeyboardType(item.type);
            int inputType = InputFieldContent.HarmonyOSInputType(keyboard, InputFieldContent.IsSecure(item.type));
            int x = xs[i];
            board.Stroke(x, 150, 140, 70, 90, 94, 110, 2);
            board.Fill(x + 2, 152, 136, 66, 80, 170, 220, 255);
            board.Text(x, 232, item.label, 210, 214, 220, 1);
            board.Text(x, 252, KeyboardLabel(keyboard), 180, 186, 196, 2);
            board.Text(x, 284, HarmonyLabel(inputType), 140, 146, 160, 1);
        }

        string path = XGuiAcceptanceScreenshot.Save("xgui-harmonyos-ime-input-type", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    [Fact]
    public void AndroidImeInputType_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "ANDROID IME INPUT TYPE", 230, 232, 240, 2);
        board.Text(40, 68, "CONTENT TYPE SELECTS NATIVE KEYBOARD", 140, 146, 160, 1);

        (string label, InputFieldContentType type)[] cases =
        [
            ("MAIL", InputFieldContentType.EmailAddress),
            ("INT", InputFieldContentType.IntegerNumber),
            ("PIN", InputFieldContentType.Pin),
            ("PASS", InputFieldContentType.Password),
        ];
        int[] xs = [48, 250, 452, 654];
        for (int i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            var keyboard = InputFieldContent.KeyboardType(item.type);
            int inputType = InputFieldContent.AndroidInputType(keyboard, InputFieldContent.IsSecure(item.type));
            int x = xs[i];
            board.Stroke(x, 150, 140, 70, 90, 94, 110, 2);
            board.Fill(x + 2, 152, 136, 66, 80, 170, 220, 255);
            board.Text(x, 232, item.label, 210, 214, 220, 1);
            board.Text(x, 252, KeyboardLabel(keyboard), 180, 186, 196, 2);
            board.Text(x, 284, inputType.ToString(), 140, 146, 160, 1);
        }

        string path = XGuiAcceptanceScreenshot.Save("xgui-android-ime-input-type", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    [Fact]
    public void InputFieldContent_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "INPUTFIELD CONTENT / SOFT KEYBOARD", 230, 232, 240, 2);
        board.Text(40, 68, "TYPE FILTER PLUS PLATFORM KEYBOARD", 140, 146, 160, 1);

        (string label, InputFieldContentType type, string raw)[] cases =
        [
            ("INT", InputFieldContentType.IntegerNumber, "12a-3"),
            ("DEC", InputFieldContentType.DecimalNumber, "12.5x"),
            ("MAIL", InputFieldContentType.EmailAddress, "a+b@c.de!"),
            ("PIN", InputFieldContentType.Pin, "12a34"),
        ];

        int[] xs = [48, 250, 452, 654];
        for (int i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            string filtered = InputFieldContent.Sanitize(item.raw, item.type);
            var keyboard = InputFieldContent.KeyboardType(item.type);
            int x = xs[i];
            board.Stroke(x, 150, 140, 70, 90, 94, 110, 2);
            board.Fill(x + 2, 152, 136, 66, 80, 170, 220, 255);
            board.Text(x, 232, item.label, 210, 214, 220, 1);
            board.Text(x, 252, filtered.Replace("+", " ").Replace("@", " ").ToUpperInvariant(), 180, 186, 196, 2);
            board.Text(x, 284, KeyboardLabel(keyboard), 140, 146, 160, 1);
        }

        string path = XGuiAcceptanceScreenshot.Save("xgui-inputfield-content-keyboard", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    private static string KeyboardLabel(TouchScreenKeyboardType keyboard) => keyboard switch
    {
        TouchScreenKeyboardType.NumberPad => "NUMPAD",
        TouchScreenKeyboardType.DecimalPad => "DEC PAD",
        TouchScreenKeyboardType.EmailAddress => "EMAIL",
        TouchScreenKeyboardType.ASCIICapable => "ASCII",
        _ => "DEFAULT",
    };

    private static string HarmonyLabel(int inputType) => inputType switch
    {
        InputFieldContent.HarmonyOSEmailAddress => "EMAIL 5",
        InputFieldContent.HarmonyOSNumber => "NUMBER 2",
        InputFieldContent.HarmonyOSNumberPassword => "NUMPW 8",
        InputFieldContent.HarmonyOSNewPassword => "NEWPW 11",
        InputFieldContent.HarmonyOSNumberDecimal => "DEC 12",
        _ => inputType.ToString(),
    };
}
