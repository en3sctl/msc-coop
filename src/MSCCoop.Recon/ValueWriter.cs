using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using UnityEngine;

namespace MSCCoop.Recon;

sealed class ValueWriter(Dictionary<Transform, string> paths)
{
    const int MaxDepth = 3;
    const int MaxListItems = 256;

    readonly Dictionary<Type, FieldInfo[]> fieldCache = new();
    readonly Dictionary<Type, PropertyInfo> valueCache = new();

    public void WriteFields(JsonWriter w, object target) => WriteFields(w, target, 0);

    public void WriteVariableValue(JsonWriter w, NamedVariable v) => Write(w, VariableValue(v), 1);

    void WriteFields(JsonWriter w, object target, int depth)
    {
        w.WriteStartObject();
        foreach (var f in Fields(target.GetType()))
        {
            w.WritePropertyName(f.Name);
            Write(w, f.GetValue(target), depth + 1);
        }
        w.WriteEndObject();
    }

    void Write(JsonWriter w, object value, int depth)
    {
        switch (value)
        {
            case null:
                w.WriteNull();
                return;
            case string s:
                w.WriteValue(s);
                return;
            case Enum e:
                w.WriteValue(e.ToString());
                return;
            case NamedVariable nv:
                WriteNamedVariable(w, nv, depth);
                return;
            case FsmEvent fe:
                WriteSingle(w, "event", fe.Name);
                return;
            case FsmOwnerDefault od:
                w.WriteStartObject();
                w.WritePropertyName("owner");
                if (od.OwnerOption == OwnerDefaultOption.UseOwner)
                    w.WriteValue("self");
                else
                    Write(w, od.GameObject, depth + 1);
                w.WriteEndObject();
                return;
            case FsmEventTarget et:
                w.WriteStartObject();
                w.WritePropertyName("target"); w.WriteValue(et.target.ToString());
                w.WritePropertyName("go"); Write(w, et.gameObject, depth + 1);
                w.WritePropertyName("fsm"); Write(w, et.fsmName, depth + 1);
                w.WriteEndObject();
                return;
            case FsmVar fv:
                if (fv.useVariable)
                    WriteSingle(w, "var", fv.variableName);
                else
                    Write(w, fv.GetValue(), depth + 1);
                return;
            case UnityEngine.Object uo:
                w.WriteValue(Describe(uo));
                return;
            case Vector2 v2:
                WriteFloats(w, v2.x, v2.y);
                return;
            case Vector3 v3:
                WriteFloats(w, v3.x, v3.y, v3.z);
                return;
            case Vector4 v4:
                WriteFloats(w, v4.x, v4.y, v4.z, v4.w);
                return;
            case Quaternion q:
                WriteFloats(w, q.x, q.y, q.z, q.w);
                return;
            case Color c:
                WriteFloats(w, c.r, c.g, c.b, c.a);
                return;
            case Rect r:
                WriteFloats(w, r.x, r.y, r.width, r.height);
                return;
            case Array { Rank: > 1 } grid:
                w.WriteValue("<" + grid.GetType().Name + ">");
                return;
            case IList list:
                WriteList(w, list, depth);
                return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || value is decimal)
            w.WriteValue(value);
        else if (depth >= MaxDepth)
            w.WriteValue("<" + type.Name + ">");
        else
            WriteFields(w, value, depth);
    }

    void WriteNamedVariable(JsonWriter w, NamedVariable nv, int depth)
    {
        if (!nv.UseVariable)
            Write(w, VariableValue(nv), depth + 1);
        else if (nv.IsNone)
            w.WriteNull();
        else
            WriteSingle(w, "var", nv.Name);
    }

    void WriteList(JsonWriter w, IList list, int depth)
    {
        w.WriteStartArray();
        var count = Math.Min(list.Count, MaxListItems);
        for (var i = 0; i < count; i++)
            Write(w, list[i], depth + 1);
        if (list.Count > MaxListItems)
            w.WriteValue("<" + (list.Count - MaxListItems) + " more>");
        w.WriteEndArray();
    }

    object VariableValue(NamedVariable v)
    {
        var type = v.GetType();
        if (!valueCache.TryGetValue(type, out var prop))
        {
            prop = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            valueCache[type] = prop;
        }
        return prop?.GetValue(v, null);
    }

    FieldInfo[] Fields(Type type)
    {
        if (!fieldCache.TryGetValue(type, out var fields))
        {
            fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            fieldCache[type] = fields;
        }
        return fields;
    }

    string Describe(UnityEngine.Object uo)
    {
        if (uo == null)
            return null;
        switch (uo)
        {
            case GameObject go:
                return paths.TryGetValue(go.transform, out var p) ? "go:" + p : "asset:" + go.name;
            case Component c:
                var owner = paths.TryGetValue(c.transform, out var cp) ? cp : "asset:" + c.name;
                return "c:" + owner + ":" + c.GetType().Name;
            default:
                return uo.GetType().Name + ":" + uo.name;
        }
    }

    static void WriteSingle(JsonWriter w, string key, string value)
    {
        w.WriteStartObject();
        w.WritePropertyName(key);
        w.WriteValue(value);
        w.WriteEndObject();
    }

    static void WriteFloats(JsonWriter w, params float[] values)
    {
        w.WriteStartArray();
        foreach (var f in values)
            w.WriteValue(f);
        w.WriteEndArray();
    }
}
