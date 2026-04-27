using System;
using System.IO;

namespace RevitFamilyBuilder.Config
{
    /// <summary>
    /// Company-wide path resolver for Autodesk Construction Cloud (ACC) assets
    /// served through Desktop Connector.
    ///
    /// <para>Design choice: every user of this tool is part of the same
    /// company, on the same ACC organisation, and uses the default Desktop
    /// Connector mount point (<c>%userprofile%\DC\ACCDocs</c>). The relative
    /// paths under that mount are therefore identical for everyone, which is
    /// why they live in this file as compile-time constants. Dynamic
    /// resolution is intentionally limited to <c>%userprofile%</c>.</para>
    ///
    /// <para>If this assumption ever breaks (multi-organisation deployment,
    /// mount-point override, etc.), introduce an external configuration
    /// system in a dedicated PR — do NOT add registry reads or environment
    /// variables here piecemeal.</para>
    /// </summary>
    public static class CompanyPaths
    {
        // Relative path from %userprofile% to the ACC Desktop Connector mount.
        private const string ACC_RELATIVE_FROM_USERPROFILE =
            @"DC\ACCDocs";

        // Relative path under ACC root to the company family template.
        private const string COMPANY_TEMPLATE_RELATIVE =
            @"Tetra Tech Inc\715-QIB - BIM_IT\Project Files\02_Gabarit\Gabarit_Famille\Metric Generic Model.rft";

        // Relative path under ACC root to the company shared-parameters file.
        // Not consumed yet — only its existence is checked at build time.
        private const string SHARED_PARAMETERS_RELATIVE =
            @"Tetra Tech Inc\715-QIB - BIM_IT\Project Files\03_Parametre_Partages\Tt_Parametres_Partages.txt";

        /// <summary>
        /// Returns the absolute path to the user's ACC Desktop Connector
        /// root, e.g. <c>C:\Users\jdoe\DC\ACCDocs</c>.
        /// </summary>
        public static string GetAccRoot()
        {
            string userProfile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ACC_RELATIVE_FROM_USERPROFILE);
        }

        /// <summary>
        /// Returns the absolute path to the company family template (.rft)
        /// stored on ACC. Caller must check <see cref="File.Exists(string)"/>
        /// before passing the result to <c>NewFamilyDocument</c>.
        /// </summary>
        public static string GetCompanyTemplatePath()
        {
            return Path.Combine(GetAccRoot(), COMPANY_TEMPLATE_RELATIVE);
        }

        /// <summary>
        /// Returns the absolute path to the company shared-parameters file
        /// stored on ACC. Caller must check <see cref="File.Exists(string)"/>;
        /// no loading is performed at this stage.
        /// </summary>
        public static string GetSharedParametersPath()
        {
            return Path.Combine(GetAccRoot(), SHARED_PARAMETERS_RELATIVE);
        }
    }
}
