// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Shader Graph node catalog (M25). Compiler and editor share these type names.
/// Master stack ports follow URP Vertex/Fragment: Position is object-space; Smoothness
/// is inverted to roughness for LightBVH <c>CalculateForwardLighting</c>.
/// </summary>
public static class ShaderGraphNodeLibrary
{
    public const string Color = "Color";
    public const string Float = "Float";
    public const string Property = "Property";
    public const string SampleTexture2D = "SampleTexture2D";

    public const string Add = "Add";
    public const string Subtract = "Subtract";
    public const string Multiply = "Multiply";
    public const string Divide = "Divide";
    public const string Lerp = "Lerp";
    public const string Saturate = "Saturate";
    public const string Power = "Power";
    public const string OneMinus = "OneMinus";
    public const string Abs = "Abs";
    public const string Clamp = "Clamp";
    public const string Split = "Split";
    public const string Combine = "Combine";

    public const string UV = "UV";
    public const string TilingAndOffset = "TilingAndOffset";

    public const string Fresnel = "Fresnel";
    public const string Position = "Position";
    public const string Normal = "Normal";
    public const string ViewDirection = "ViewDirection";
    public const string VertexColor = "VertexColor";

    public const string MasterUnlit = "MasterUnlit";
    public const string MasterLit = "MasterLit";
    public const string MasterDecal = "MasterDecal";
    public const string MasterFullscreen = "MasterFullscreen";

    public static readonly string[] AllTypes =
    [
        Color, Float, Property, SampleTexture2D,
        Add, Subtract, Multiply, Divide, Lerp, Saturate, Power, OneMinus, Abs, Clamp, Split, Combine,
        UV, TilingAndOffset,
        Fresnel, Position, Normal, ViewDirection, VertexColor,
        MasterUnlit, MasterLit, MasterDecal, MasterFullscreen,
    ];

    public static bool IsKnown(string type)
    {
        if (string.IsNullOrEmpty(type))
            return false;
        for (int i = 0; i < AllTypes.Length; i++)
        {
            if (string.Equals(AllTypes[i], type, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Fills input/output port ids for the editor NodeGraph (M26).</summary>
    public static void GetPorts(string type, List<string> inputs, List<string> outputs)
    {
        inputs.Clear();
        outputs.Clear();
        if (string.Equals(type, Color, StringComparison.Ordinal))
        {
            outputs.Add("Out"); outputs.Add("RGB"); outputs.Add("R"); outputs.Add("G"); outputs.Add("B"); outputs.Add("A");
            return;
        }
        if (string.Equals(type, Float, StringComparison.Ordinal)
            || string.Equals(type, Position, StringComparison.Ordinal)
            || string.Equals(type, Normal, StringComparison.Ordinal)
            || string.Equals(type, ViewDirection, StringComparison.Ordinal)
            || string.Equals(type, VertexColor, StringComparison.Ordinal)
            || string.Equals(type, UV, StringComparison.Ordinal))
        {
            outputs.Add("Out");
            if (string.Equals(type, UV, StringComparison.Ordinal))
            {
                outputs.Add("X"); outputs.Add("Y");
            }
            return;
        }
        if (string.Equals(type, Property, StringComparison.Ordinal))
        {
            outputs.Add("Out"); outputs.Add("RGB");
            return;
        }
        if (string.Equals(type, SampleTexture2D, StringComparison.Ordinal))
        {
            inputs.Add("UV");
            outputs.Add("RGBA"); outputs.Add("RGB"); outputs.Add("A");
            return;
        }
        if (string.Equals(type, Add, StringComparison.Ordinal)
            || string.Equals(type, Subtract, StringComparison.Ordinal)
            || string.Equals(type, Multiply, StringComparison.Ordinal)
            || string.Equals(type, Divide, StringComparison.Ordinal))
        {
            inputs.Add("A"); inputs.Add("B"); outputs.Add("Out");
            return;
        }
        if (string.Equals(type, Lerp, StringComparison.Ordinal))
        {
            inputs.Add("A"); inputs.Add("B"); inputs.Add("T"); outputs.Add("Out");
            return;
        }
        if (string.Equals(type, Saturate, StringComparison.Ordinal)
            || string.Equals(type, OneMinus, StringComparison.Ordinal)
            || string.Equals(type, Abs, StringComparison.Ordinal))
        {
            inputs.Add("In"); outputs.Add("Out");
            return;
        }
        if (string.Equals(type, Power, StringComparison.Ordinal))
        {
            inputs.Add("A"); inputs.Add("B"); outputs.Add("Out");
            return;
        }
        if (string.Equals(type, Clamp, StringComparison.Ordinal))
        {
            inputs.Add("In"); inputs.Add("Min"); inputs.Add("Max"); outputs.Add("Out");
            return;
        }
        if (string.Equals(type, Split, StringComparison.Ordinal))
        {
            inputs.Add("In");
            outputs.Add("R"); outputs.Add("G"); outputs.Add("B"); outputs.Add("A"); outputs.Add("RGB");
            return;
        }
        if (string.Equals(type, Combine, StringComparison.Ordinal))
        {
            inputs.Add("R"); inputs.Add("G"); inputs.Add("B"); inputs.Add("A"); outputs.Add("Out");
            return;
        }
        if (string.Equals(type, TilingAndOffset, StringComparison.Ordinal))
        {
            inputs.Add("UV"); inputs.Add("Tiling"); inputs.Add("Offset"); outputs.Add("Out");
            return;
        }
        if (string.Equals(type, Fresnel, StringComparison.Ordinal))
        {
            inputs.Add("Normal"); inputs.Add("ViewDir"); inputs.Add("Power"); outputs.Add("Out");
            return;
        }
        if (string.Equals(type, MasterUnlit, StringComparison.Ordinal))
        {
            inputs.Add("Albedo"); inputs.Add("Alpha"); inputs.Add("Position");
            return;
        }
        if (string.Equals(type, MasterLit, StringComparison.Ordinal))
        {
            inputs.Add("Albedo"); inputs.Add("Normal"); inputs.Add("Emission");
            inputs.Add("Metallic"); inputs.Add("Smoothness"); inputs.Add("Occlusion");
            inputs.Add("Alpha"); inputs.Add("Position");
            return;
        }
        if (string.Equals(type, MasterDecal, StringComparison.Ordinal))
        {
            inputs.Add("Albedo"); inputs.Add("Normal"); inputs.Add("Alpha");
            inputs.Add("Metallic"); inputs.Add("Smoothness");
            return;
        }
        if (string.Equals(type, MasterFullscreen, StringComparison.Ordinal))
        {
            inputs.Add("Color");
        }
    }
}
