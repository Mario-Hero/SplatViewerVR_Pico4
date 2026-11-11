// SPDX-License-Identifier: MIT
using System;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-only helper to decode a splat file and report the statistics of the
/// decoded splats. Used to tell "file decoded wrong" apart from "file rendered wrong".
/// </summary>
public static class SogDiagnostic
{
    public static string Diagnose(string path)
    {
        var readerType = Type.GetType("PlayCanvasSogReader, Assembly-CSharp");
        if (readerType == null)
            return "READER_NOT_FOUND";

        var readMethod = readerType.GetMethod("ReadFile", BindingFlags.Public | BindingFlags.Static);
        if (readMethod == null)
            return "READ_METHOD_NOT_FOUND";

        Array splats;
        try
        {
            splats = (Array)readMethod.Invoke(null, new object[] { path });
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            return "DECODE_FAILED: " + inner.GetType().Name + ": " + inner.Message + "\n" + inner.StackTrace;
        }

        if (splats == null || splats.Length == 0)
            return "DECODE_EMPTY";

        var sb = new StringBuilder();
        sb.AppendLine("count=" + splats.Length);

        Type dataType = splats.GetType().GetElementType();
        FieldInfo fPos = dataType.GetField("pos");
        FieldInfo fScale = dataType.GetField("scale");
        FieldInfo fRot = dataType.GetField("rot");
        FieldInfo fDc = dataType.GetField("dc0");
        FieldInfo fOpacity = dataType.GetField("opacity");

        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        double opacitySum = 0, opacityMin = 1e9, opacityMax = -1e9, opacityZero = 0;
        double scaleSum = 0, scaleMin = 1e9, scaleMax = -1e9;
        double dcSum = 0, dcMin = 1e9, dcMax = -1e9;
        int nanPos = 0;

        for (int i = 0; i < splats.Length; i++)
        {
            object s = splats.GetValue(i);

            object posObj = fPos.GetValue(s);
            Vector3 p = ToVector3(posObj);
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
            {
                nanPos++;
            }
            else
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            float op = (float)fOpacity.GetValue(s);
            opacitySum += op;
            opacityMin = Math.Min(opacityMin, op);
            opacityMax = Math.Max(opacityMax, op);
            if (op <= 0.0001f) opacityZero++;

            Vector3 sc = ToVector3(fScale.GetValue(s));
            float scMaxComp = Math.Max(Mathf.Abs(sc.x), Math.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)));
            scaleSum += scMaxComp;
            scaleMin = Math.Min(scaleMin, scMaxComp);
            scaleMax = Math.Max(scaleMax, scMaxComp);

            Vector3 dc = ToVector3(fDc.GetValue(s));
            float dcMaxComp = Math.Max(Mathf.Abs(dc.x), Math.Max(Mathf.Abs(dc.y), Mathf.Abs(dc.z)));
            dcSum += dcMaxComp;
            dcMin = Math.Min(dcMin, dcMaxComp);
            dcMax = Math.Max(dcMax, dcMaxComp);
        }

        sb.AppendLine("pos min=" + min.ToString("F3") + " max=" + max.ToString("F3") + " nan=" + nanPos);
        sb.AppendLine("opacity min=" + opacityMin.ToString("F4") + " avg=" + (opacitySum / splats.Length).ToString("F4") +
                      " max=" + opacityMax.ToString("F4") + " zero=" + opacityZero);
        sb.AppendLine("scale(maxComp) min=" + scaleMin.ToString("F6") + " avg=" + (scaleSum / splats.Length).ToString("F6") +
                      " max=" + scaleMax.ToString("F6"));
        sb.AppendLine("dc(maxComp) min=" + dcMin.ToString("F4") + " avg=" + (dcSum / splats.Length).ToString("F4") +
                      " max=" + dcMax.ToString("F4"));
        return sb.ToString();
    }

    static Vector3 ToVector3(object value)
    {
        if (value is Vector3 v) return v;
        if (value is null) return Vector3.zero;
        var t = value.GetType();
        var x = (float)t.GetField("x").GetValue(value);
        var y = (float)t.GetField("y").GetValue(value);
        var z = (float)t.GetField("z").GetValue(value);
        return new Vector3(x, y, z);
    }
}
