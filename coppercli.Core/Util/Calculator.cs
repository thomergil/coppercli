using coppercli.Core.Communication;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;

namespace coppercli.Core.Util
{
    public class Calculator
    {
        private Machine machine;
        public Func<GCode.GCodeFile> GetGCode;
        private bool Success = true;

        public Calculator(Machine machine)
        {
            this.machine = machine;
        }

        private static string[] Axes = new string[] { "X", "Y", "Z" };

        private string ExpressionEvaluator(string input)
        {
            try
            {
                // Build variable dictionary
                Dictionary<string, double> variables = new Dictionary<string, double>();
                for (int i = 0; i < 3; i++)
                {
                    variables.Add("M" + Axes[i], machine.MachinePosition[i]);
                    variables.Add("W" + Axes[i], machine.WorkPosition[i]);
                    variables.Add("PM" + Axes[i], machine.LastProbePosMachine[i]);
                    variables.Add("PW" + Axes[i], machine.LastProbePosWork[i]);
                }

                if (GetGCode != null)
                {
                    try
                    {
                        var file = GetGCode();
                        var min = file.MinFeed;
                        var max = file.MaxFeed;
                        if (!file.ContainsMotion)
                        {
                            min = new Vector3(0, 0, 0);
                            max = new Vector3(0, 0, 0);
                        }
                        for (int i = 0; i < 3; i++)
                        {
                            variables.Add("MAX" + Axes[i], max[i]);
                            variables.Add("MIN" + Axes[i], min[i]);
                        }
                    }
                    catch { }
                }

                variables.Add("TLO", machine.CurrentTLO);

                // Replace variables in expression
                string expression = input;
                foreach (var kvp in variables)
                {
                    expression = expression.Replace(kvp.Key, kvp.Value.ToString(Constants.DecimalOutputFormat));
                }

                // Evaluate using DataTable.Compute (simple but effective)
                DataTable dt = new DataTable();
                object result = dt.Compute(expression, "");
                double value = Convert.ToDouble(result);

                return value.ToString("0.###", Constants.DecimalOutputFormat);
            }
            catch (Exception ex)
            {
                Success = false;
                Console.WriteLine(ex.Message);
                return $"[{ex.Message}]";
            }
        }

        public string Evaluate(string input, out bool success)
        {
            Success = true;

            try
            {
                int depth = 0;
                int start = 0;

                StringBuilder output = new StringBuilder(input.Length);

                for (int i = 0; i < input.Length; i++)
                {
                    if (input[i] == '(')
                    {
                        if (depth == 0)
                            start = i + 1;
                        depth++;
                    }
                    else if (input[i] == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            if (i - start > 0)
                                output.Append(ExpressionEvaluator(input.Substring(start, i - start)));
                        }
                        else if (depth == -1)
                        {
                            Success = false;
                            depth = 0;
                        }
                    }
                    else if (depth == 0)
                        output.Append(input[i]);
                }

                if (depth != 0)
                    Success = false;

                success = Success;
                return output.ToString();
            }
            catch
            {
                success = false;
                return "ERROR";
            }
        }
    }
}
