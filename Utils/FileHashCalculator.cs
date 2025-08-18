using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Utils
{
    /// <summary>
    /// Utility class for calculating file hashes to detect changes
    /// Provides different hashing strategies for Revit files
    /// TODO: Choose appropriate hashing method based on performance requirements
    /// </summary>
    public static class FileHashCalculator
    {
        #region Public Methods

        /// <summary>
        /// Calculates hash for a Revit document
        /// Uses a combination of file metadata and content for accurate change detection
        /// </summary>
        /// <param name="document">Revit document to hash</param>
        /// <returns>Hash string representing the current state of the document</returns>
        public static string CalculateHash(Document document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            try
            {
                // Strategy 1: If document is saved, use file-based hash
                var filePath = document.PathName;
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath) && !document.IsModified)
                {
                    return CalculateFileHash(filePath);
                }

                // Strategy 2: For unsaved or modified documents, use metadata hash
                return CalculateMetadataHash(document);
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                // If hash calculation fails, return a unique hash to force sync
                return $"error-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            }
        }

        /// <summary>
        /// Calculates hash for a file on disk
        /// Uses SHA-256 for reliable file change detection
        /// </summary>
        /// <param name="filePath">Path to file</param>
        /// <returns>SHA-256 hash of file content</returns>
        public static string CalculateFileHash(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            try
            {
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hashBytes = sha256.ComputeHash(stream);
                    return ConvertHashToString(hashBytes);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to calculate file hash: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Calculates hash for file metadata
        /// Useful for quickly detecting changes without reading entire file
        /// </summary>
        /// <param name="filePath">Path to file</param>
        /// <returns>Hash based on file metadata</returns>
        public static string CalculateMetadataOnlyHash(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            try
            {
                var fileInfo = new FileInfo(filePath);
                var metadataString = $"{fileInfo.Length}|{fileInfo.LastWriteTimeUtc:O}|{fileInfo.CreationTimeUtc:O}";

                using (var sha256 = SHA256.Create())
                {
                    var inputBytes = Encoding.UTF8.GetBytes(metadataString);
                    var hashBytes = sha256.ComputeHash(inputBytes);
                    return ConvertHashToString(hashBytes);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to calculate metadata hash: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Fast hash calculation using file size and modification time
        /// Much faster than content hash but less reliable for change detection
        /// TODO: Use only when performance is critical and occasional false negatives are acceptable
        /// </summary>
        /// <param name="filePath">Path to file</param>
        /// <returns>Fast hash based on basic file properties</returns>
        public static string CalculateFastHash(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            try
            {
                var fileInfo = new FileInfo(filePath);
                
                // Simple hash based on size and modification time
                var hashCode = HashCode.Combine(
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc.Ticks,
                    fileInfo.Name);

                return hashCode.ToString("X8");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to calculate fast hash: {ex.Message}", ex);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Calculates hash based on Revit document metadata
        /// Used when file-based hash is not available (unsaved documents)
        /// </summary>
        /// <param name="document">Revit document</param>
        /// <returns>Hash based on document metadata and content summary</returns>
        private static string CalculateMetadataHash(Document document)
        {
            try
            {
                var hashData = new StringBuilder();

                // Basic document information
                hashData.Append(document.Title ?? "");
                hashData.Append("|");

                // Project information
                var projectInfo = document.ProjectInformation;
                if (projectInfo != null)
                {
                    hashData.Append(GetParameterValue(projectInfo, BuiltInParameter.PROJECT_NAME) ?? "");
                    hashData.Append("|");
                    hashData.Append(GetParameterValue(projectInfo, BuiltInParameter.PROJECT_NUMBER) ?? "");
                    hashData.Append("|");
                    hashData.Append(GetParameterValue(projectInfo, BuiltInParameter.CLIENT_NAME) ?? "");
                    hashData.Append("|");
                }

                // Element counts (quick way to detect major changes)
                var collector = new FilteredElementCollector(document);
                var elementCount = collector.WhereElementIsNotElementType().GetElementCount();
                hashData.Append(elementCount);
                hashData.Append("|");

                // View count
                var viewCollector = new FilteredElementCollector(document);
                var viewCount = viewCollector.OfClass(typeof(View)).GetElementCount();
                hashData.Append(viewCount);
                hashData.Append("|");

                // Document GUID (unique identifier for the document)
                if (document.Application.VersionNumber != null)
                {
                    hashData.Append(document.Application.VersionNumber);
                    hashData.Append("|");
                }

                // Add current timestamp for unsaved documents to ensure uniqueness
                if (document.IsModified)
                {
                    hashData.Append(DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                    hashData.Append("|");
                }

                // TODO: Add more specific elements that are important for your use case
                // For example: specific family instances, key parameters, etc.

                using (var sha256 = SHA256.Create())
                {
                    var inputBytes = Encoding.UTF8.GetBytes(hashData.ToString());
                    var hashBytes = sha256.ComputeHash(inputBytes);
                    return ConvertHashToString(hashBytes);
                }
            }
            catch (Exception ex)
            {
                // If metadata hash fails, generate a time-based hash
                var fallbackHash = $"metadata-error-{DateTime.UtcNow:yyyyMMddHHmmss}";
                // TODO: Log the exception
                return CalculateStringHash(fallbackHash);
            }
        }

        /// <summary>
        /// Gets parameter value from element safely
        /// </summary>
        /// <param name="element">Element to get parameter from</param>
        /// <param name="parameter">Built-in parameter to retrieve</param>
        /// <returns>Parameter value as string or null</returns>
        private static string GetParameterValue(Element element, BuiltInParameter parameter)
        {
            try
            {
                var param = element.get_Parameter(parameter);
                if (param != null && param.HasValue)
                {
                    switch (param.StorageType)
                    {
                        case StorageType.String:
                            return param.AsString();
                        case StorageType.Integer:
                            return param.AsInteger().ToString();
                        case StorageType.Double:
                            return param.AsDouble().ToString("F6");
                        case StorageType.ElementId:
                            return param.AsElementId()?.ToString();
                        default:
                            return param.AsValueString();
                    }
                }
            }
            catch
            {
                // Ignore parameter access errors
            }
            return null;
        }

        /// <summary>
        /// Calculates SHA-256 hash for a string
        /// </summary>
        /// <param name="input">String to hash</param>
        /// <returns>SHA-256 hash</returns>
        private static string CalculateStringHash(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            using (var sha256 = SHA256.Create())
            {
                var inputBytes = Encoding.UTF8.GetBytes(input);
                var hashBytes = sha256.ComputeHash(inputBytes);
                return ConvertHashToString(hashBytes);
            }
        }

        /// <summary>
        /// Converts hash byte array to hex string
        /// </summary>
        /// <param name="hashBytes">Hash bytes</param>
        /// <returns>Hex string representation</returns>
        private static string ConvertHashToString(byte[] hashBytes)
        {
            var sb = new StringBuilder();
            foreach (byte b in hashBytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        #endregion

        #region Validation Methods

        /// <summary>
        /// Validates that a hash string is in the expected format
        /// </summary>
        /// <param name="hash">Hash string to validate</param>
        /// <returns>True if hash appears valid</returns>
        public static bool IsValidHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return false;

            // SHA-256 hashes are 64 hex characters
            if (hash.Length == 64)
            {
                return IsHexString(hash);
            }

            // Fast hashes are 8 hex characters
            if (hash.Length == 8)
            {
                return IsHexString(hash);
            }

            // Special case for error hashes (contain "error-" prefix)
            if (hash.StartsWith("error-") || hash.StartsWith("metadata-error-"))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if string contains only hexadecimal characters
        /// </summary>
        /// <param name="input">String to check</param>
        /// <returns>True if string is valid hex</returns>
        private static bool IsHexString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;

            foreach (char c in input)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        #endregion
    }
}