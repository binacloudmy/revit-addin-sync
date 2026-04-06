using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Manages BINA shared parameters for cost data on Revit elements.
    /// Creates parameters once per document, idempotent on subsequent calls.
    /// </summary>
    public static class BINASharedParameters
    {
        public const string PARAM_UNIT_PRICE = "BINA_Unit_Price";
        public const string PARAM_TOTAL_COST = "BINA_Total_Cost";
        public const string PARAM_JKR_CODE = "BINA_JKR_Code";
        public const string PARAM_PRICE_SOURCE = "BINA_Price_Source";

        private const string GROUP_NAME = "BINA Cost";

        /// <summary>
        /// All priceable categories that get cost parameters bound to them.
        /// Matches the categories used by RevitModelWalker.
        /// </summary>
        private static readonly BuiltInCategory[] AllCategories = new[]
        {
            // Area-based
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_CurtainWallPanels,
            // Count-based
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_Casework,
            BuiltInCategory.OST_Furniture,
            BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_SpecialityEquipment,
            // Length-based
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_CableTray,
        };

        /// <summary>
        /// Ensure all BINA cost parameters exist in the document.
        /// Must be called inside a Transaction.
        /// Idempotent — safe to call multiple times.
        /// </summary>
        public static void EnsureParameters(Document doc)
        {
            // Check if parameters already exist by testing one element
            if (ParametersExist(doc))
                return;

            // Save current shared param file path (restore after)
            string originalFile = doc.Application.SharedParametersFilename;
            string tempFile = null;

            try
            {
                // Create a temporary shared parameter file
                tempFile = Path.Combine(Path.GetTempPath(), "BINA_SharedParams.txt");
                File.WriteAllText(tempFile, "");
                doc.Application.SharedParametersFilename = tempFile;

                var defFile = doc.Application.OpenSharedParameterFile();
                if (defFile == null)
                    throw new InvalidOperationException("Failed to create shared parameter file");

                // Create or get the BINA Cost group
                var group = defFile.Groups.get_Item(GROUP_NAME)
                    ?? defFile.Groups.Create(GROUP_NAME);

                // Build category set for binding
                var catSet = new CategorySet();
                foreach (var bic in AllCategories)
                {
                    var cat = Category.GetCategory(doc, bic);
                    if (cat != null)
                        catSet.Insert(cat);
                }

                if (catSet.Size == 0)
                    return;

                var binding = doc.Application.Create.NewInstanceBinding(catSet);

                // Create each parameter
                CreateAndBind(doc, group, binding, PARAM_UNIT_PRICE, SpecTypeId.Number, false);
                CreateAndBind(doc, group, binding, PARAM_TOTAL_COST, SpecTypeId.Number, false);
                CreateAndBind(doc, group, binding, PARAM_JKR_CODE, SpecTypeId.String.Text, true);
                CreateAndBind(doc, group, binding, PARAM_PRICE_SOURCE, SpecTypeId.String.Text, true);
            }
            finally
            {
                // Restore original shared parameter file
                if (!string.IsNullOrEmpty(originalFile) && File.Exists(originalFile))
                    doc.Application.SharedParametersFilename = originalFile;

                // Clean up temp file
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        /// <summary>
        /// Check if BINA parameters already exist in the document.
        /// </summary>
        public static bool ParametersExist(Document doc)
        {
            var iter = doc.ParameterBindings.ForwardIterator();
            while (iter.MoveNext())
            {
                if (iter.Key is ExternalDefinition extDef && extDef.Name == PARAM_UNIT_PRICE)
                    return true;
                if (iter.Key is InternalDefinition intDef && intDef.Name == PARAM_UNIT_PRICE)
                    return true;
            }
            return false;
        }

        private static void CreateAndBind(
            Document doc,
            DefinitionGroup group,
            InstanceBinding binding,
            string paramName,
            ForgeTypeId specTypeId,
            bool isText)
        {
            // Check if already exists in this group
            var existingDef = group.Definitions.get_Item(paramName);
            if (existingDef != null)
            {
                // Already defined, just ensure it's bound
                if (doc.ParameterBindings.get_Item(existingDef) == null)
                    doc.ParameterBindings.Insert(existingDef, binding, BuiltInParameterGroup.PG_DATA);
                return;
            }

            // Create new definition
            var options = new ExternalDefinitionCreationOptions(paramName, specTypeId)
            {
                Visible = true,
                UserModifiable = true,
            };

            var definition = group.Definitions.Create(options);
            doc.ParameterBindings.Insert(definition, binding, BuiltInParameterGroup.PG_DATA);
        }
    }
}
