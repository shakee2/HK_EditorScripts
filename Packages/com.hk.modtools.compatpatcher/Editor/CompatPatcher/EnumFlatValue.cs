using System;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace HK.CompatPatcher
{
    /// <summary>
    /// Canonical flat-string form for enum leaves in StructDiff / DiffGui.
    /// Ordinary enums: <c>0 = Tier1</c>. <see cref="FlagsAttribute"/> enums stay bare ints
    /// (bitmasks / combinations — a single name would be wrong).
    /// </summary>
    static class EnumFlatValue
    {
        public const string Sep = " = ";

        const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static string Format(SerializedProperty prop)
        {
            int v = prop.intValue;
            string num = v.ToString(CultureInfo.InvariantCulture);
            var et = TryResolveEnumType(prop);
            if (et != null && et.IsDefined(typeof(FlagsAttribute), inherit: false))
                return num;
            if (et != null)
            {
                string name = Enum.GetName(et, v);
                return name != null ? num + Sep + name : num;
            }
            if (prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length)
            {
                string name = prop.enumNames[prop.enumValueIndex];
                if (!string.IsNullOrEmpty(name))
                    return num + Sep + name;
            }
            return num;
        }

        public static string Format(Type enumType, object val)
        {
            if (enumType == null || !enumType.IsEnum || val == null)
                return Convert.ToString(val, CultureInfo.InvariantCulture) ?? "";
            long lv = Convert.ToInt64(val, CultureInfo.InvariantCulture);
            string num = lv.ToString(CultureInfo.InvariantCulture);
            if (enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
                return num;
            string name = Enum.GetName(enumType, val);
            return name != null ? num + Sep + name : num;
        }

        /// <summary>Bare int, or leading int from <c>0 = Tier1</c>.</summary>
        public static bool TryParseInt(string value, out int iv)
        {
            iv = 0;
            if (string.IsNullOrEmpty(value)) return false;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv))
                return true;
            int sep = value.IndexOf(Sep, StringComparison.Ordinal);
            return sep > 0
                   && int.TryParse(value.Substring(0, sep), NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out iv);
        }

        /// <summary>Name after <c> = </c>, or the whole string when it is not an int form.</summary>
        public static bool TryNamePart(string value, out string name)
        {
            name = null;
            if (string.IsNullOrEmpty(value)) return false;
            int sep = value.IndexOf(Sep, StringComparison.Ordinal);
            if (sep >= 0)
            {
                name = value.Substring(sep + Sep.Length);
                return !string.IsNullOrEmpty(name);
            }
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return false;
            name = value;
            return true;
        }

        /// <summary>
        /// StructDiff equality: <c>0</c> (YAML) matches <c>0 = Tier1</c> (live).
        /// </summary>
        public static bool Equal(string a, string b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (TryParseInt(a, out int ia) && TryParseInt(b, out int ib))
                return ia == ib;
            return false;
        }

        static Type TryResolveEnumType(SerializedProperty prop)
        {
            var target = prop.serializedObject != null ? prop.serializedObject.targetObject : null;
            if (target == null || string.IsNullOrEmpty(prop.propertyPath)) return null;
            try
            {
                Type type = target.GetType();
                // Unity: list.Array.data[0].field → list.[0].field
                string path = prop.propertyPath.Replace(".Array.data[", ".[");
                foreach (var raw in path.Split('.'))
                {
                    if (raw.Length == 0) continue;
                    if (raw[0] == '[' && raw[raw.Length - 1] == ']')
                        continue; // element index — type already narrowed to element
                    FieldInfo fi = null;
                    for (var t = type; t != null; t = t.BaseType)
                    {
                        fi = t.GetField(raw, FieldFlags);
                        if (fi != null) break;
                    }
                    if (fi == null) return null;
                    type = fi.FieldType;
                    if (type.IsArray) type = type.GetElementType();
                    else if (type.IsGenericType)
                    {
                        var args = type.GetGenericArguments();
                        if (args.Length == 1)
                            type = args[0];
                    }
                    if (type == null) return null;
                }
                return type.IsEnum ? type : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
