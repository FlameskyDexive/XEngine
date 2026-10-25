// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.UI;

/// <summary>Soft-keyboard layout requested by a focused InputField.</summary>
public enum TouchScreenKeyboardType
{
    Default,
    ASCIICapable,
    NumbersAndPunctuation,
    URL,
    NumberPad,
    PhonePad,
    NamePhonePad,
    EmailAddress,
    DecimalPad,
    Search,
}

/// <summary>High-level InputField content filter. Also selects the matching soft keyboard.</summary>
public enum InputFieldContentType
{
    Standard,
    IntegerNumber,
    DecimalNumber,
    Alphanumeric,
    Name,
    EmailAddress,
    Password,
    Pin,
}

/// <summary>Filters and keyboard mapping shared by package and host input fields.</summary>
public static class InputFieldContent
{
    public static TouchScreenKeyboardType KeyboardType(InputFieldContentType contentType) => contentType switch
    {
        InputFieldContentType.IntegerNumber => TouchScreenKeyboardType.NumberPad,
        InputFieldContentType.DecimalNumber => TouchScreenKeyboardType.DecimalPad,
        InputFieldContentType.Alphanumeric => TouchScreenKeyboardType.ASCIICapable,
        InputFieldContentType.Name => TouchScreenKeyboardType.NamePhonePad,
        InputFieldContentType.EmailAddress => TouchScreenKeyboardType.EmailAddress,
        InputFieldContentType.Password => TouchScreenKeyboardType.Default,
        InputFieldContentType.Pin => TouchScreenKeyboardType.NumberPad,
        _ => TouchScreenKeyboardType.Default,
    };

    public static bool IsSecure(InputFieldContentType contentType)
        => contentType is InputFieldContentType.Password or InputFieldContentType.Pin;

    // Android.Text.InputTypes values. Kept as integers so Runtime stays free of Android bindings.
    public const int AndroidClassText = 1;
    public const int AndroidClassNumber = 2;
    public const int AndroidClassPhone = 3;
    public const int AndroidTextVariationUri = 16;
    public const int AndroidTextVariationEmailAddress = 32;
    public const int AndroidTextVariationPassword = 128;
    public const int AndroidTextVariationVisiblePassword = 144;
    public const int AndroidTextVariationPersonName = 96;
    public const int AndroidTextVariationFilter = 176;
    public const int AndroidTextFlagCapWords = 8192;
    public const int AndroidTextFlagMultiLine = 131072;
    public const int AndroidTextFlagNoSuggestions = 524288;
    public const int AndroidNumberFlagSigned = 4096;
    public const int AndroidNumberFlagDecimal = 8192;
    public const int AndroidNumberVariationPassword = 16;

    public static int AndroidInputType(TouchScreenKeyboardType keyboardType, bool secure)
    {
        int inputType = keyboardType switch
        {
            TouchScreenKeyboardType.NumberPad => AndroidClassNumber | AndroidNumberFlagSigned,
            TouchScreenKeyboardType.DecimalPad => AndroidClassNumber | AndroidNumberFlagSigned | AndroidNumberFlagDecimal,
            TouchScreenKeyboardType.PhonePad => AndroidClassPhone,
            TouchScreenKeyboardType.EmailAddress => AndroidClassText | AndroidTextVariationEmailAddress,
            TouchScreenKeyboardType.URL => AndroidClassText | AndroidTextVariationUri,
            TouchScreenKeyboardType.NamePhonePad => AndroidClassText | AndroidTextVariationPersonName | AndroidTextFlagCapWords,
            TouchScreenKeyboardType.Search => AndroidClassText | AndroidTextVariationFilter,
            TouchScreenKeyboardType.ASCIICapable => AndroidClassText | AndroidTextFlagNoSuggestions,
            TouchScreenKeyboardType.NumbersAndPunctuation => AndroidClassText | AndroidTextFlagNoSuggestions,
            _ => AndroidClassText,
        };

        if (secure)
        {
            if ((inputType & AndroidClassNumber) == AndroidClassNumber)
                inputType |= AndroidNumberVariationPassword;
            else
                inputType = AndroidClassText | AndroidTextVariationPassword | AndroidTextFlagNoSuggestions;
        }

        return inputType;
    }

    // OpenHarmony InputType values from @ohos.inputMethodEngine. Kept as integers so Runtime
    // stays free of HarmonyOS bindings. Native/ETS hosts can pass the result straight through.
    public const int HarmonyOSNone = -1;
    public const int HarmonyOSText = 0;
    public const int HarmonyOSMultiline = 1;
    public const int HarmonyOSNumber = 2;
    public const int HarmonyOSPhone = 3;
    public const int HarmonyOSDateTime = 4;
    public const int HarmonyOSEmailAddress = 5;
    public const int HarmonyOSUrl = 6;
    public const int HarmonyOSVisiblePassword = 7;
    public const int HarmonyOSNumberPassword = 8;
    public const int HarmonyOSScreenLockPassword = 9;
    public const int HarmonyOSUserName = 10;
    public const int HarmonyOSNewPassword = 11;
    public const int HarmonyOSNumberDecimal = 12;

    public static int HarmonyOSInputType(TouchScreenKeyboardType keyboardType, bool secure)
    {
        if (secure)
        {
            return keyboardType is TouchScreenKeyboardType.NumberPad or TouchScreenKeyboardType.DecimalPad
                ? HarmonyOSNumberPassword
                : HarmonyOSNewPassword;
        }

        return keyboardType switch
        {
            TouchScreenKeyboardType.NumberPad => HarmonyOSNumber,
            TouchScreenKeyboardType.DecimalPad => HarmonyOSNumberDecimal,
            TouchScreenKeyboardType.PhonePad => HarmonyOSPhone,
            TouchScreenKeyboardType.EmailAddress => HarmonyOSEmailAddress,
            TouchScreenKeyboardType.URL => HarmonyOSUrl,
            TouchScreenKeyboardType.NamePhonePad => HarmonyOSUserName,
            TouchScreenKeyboardType.Search => HarmonyOSText,
            TouchScreenKeyboardType.ASCIICapable => HarmonyOSText,
            TouchScreenKeyboardType.NumbersAndPunctuation => HarmonyOSText,
            _ => HarmonyOSText,
        };
    }

    public static string Sanitize(string? value, InputFieldContentType contentType, int characterLimit = 0)
    {
        string input = value ?? string.Empty;
        if (input.IndexOf('\n') >= 0 || input.IndexOf('\r') >= 0)
            input = input.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

        System.Text.StringBuilder? builder = null;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (!IsAllowed(c, contentType))
            {
                builder ??= new System.Text.StringBuilder(input.Length);
                if (builder.Length == 0 && i > 0)
                    builder.Append(input, 0, i);
                continue;
            }
            builder?.Append(c);
        }
        if (builder != null)
            input = builder.ToString();

        if (characterLimit > 0 && input.Length > characterLimit)
            input = input[..characterLimit];
        return input;
    }

    public static bool IsAllowed(char c, InputFieldContentType contentType) => contentType switch
    {
        InputFieldContentType.IntegerNumber => char.IsDigit(c) || c is '+' or '-',
        InputFieldContentType.DecimalNumber => char.IsDigit(c) || c is '+' or '-' or '.' or ',',
        InputFieldContentType.Alphanumeric => char.IsLetterOrDigit(c),
        InputFieldContentType.Name => char.IsLetter(c) || c is ' ' or '-' or '\'',
        InputFieldContentType.EmailAddress => char.IsLetterOrDigit(c) || c is '.' or '@' or '_' or '+' or '-',
        InputFieldContentType.Pin => char.IsDigit(c),
        _ => true,
    };
}
