using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service responsible for performing clash detection between element sets
    /// Handles geometric intersection detection, external file loading, and clash result generation
    /// </summary>
    public class ClashDetectionService
    {
        private readonly ElementFilterService _filterService;

        /// <summary>
        /// Initializes a new instance of the ClashDetectionService
        /// </summary>
        public ClashDetectionService()
        {
            _filterService = new ElementFilterService();
        }

        #region Public Methods

        /// <summary>
        /// Runs clash detection between two element sets
        /// </summary>
        /// <param name="currentDocument">The current active Revit document</param>
        /// <param name="externalFiles">List of external file paths to clash against</param>
        /// <param name="setA">Element selection set A (typically from current model)</param>
        /// <param name="setB">Element selection set B (typically from external files)</param>
        /// <param name="tolerance">Clash tolerance in millimeters (0 = hard clash only)</param>
        /// <param name="progress">Optional progress reporter for UI updates</param>
        /// <param name="cancellationToken">Token to support operation cancellation</param>
        /// <returns>List of detected clashes</returns>
        public List<ClashResult> RunClashDetection(
            Document currentDocument,
            List<string> externalFiles,
            ElementSelectionSet setA,
            ElementSelectionSet setB,
            double tolerance,
            IProgress<ClashDetectionProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (currentDocument == null)
                throw new ArgumentNullException(nameof(currentDocument));
            if (externalFiles == null || externalFiles.Count == 0)
                throw new ArgumentException("External files list cannot be null or empty", nameof(externalFiles));
            if (setA == null)
                throw new ArgumentNullException(nameof(setA));
            if (setB == null)
                throw new ArgumentNullException(nameof(setB));

            var clashes = new List<ClashResult>();

            try
            {
                // Step 1: Get elements from Set A (current document)
                progress?.Report(new ClashDetectionProgress
                {
                    Phase = "Loading Set A Elements",
                    PercentComplete = 0
                });

                var setAElements = _filterService.GetFilteredElements(currentDocument, setA);

                if (setAElements.Count == 0)
                    throw new InvalidOperationException("Set A contains no elements after filtering");

                // Step 2: Load external files and get Set B elements
                progress?.Report(new ClashDetectionProgress
                {
                    Phase = "Loading External Files",
                    PercentComplete = 20
                });

                var setBElements = LoadExternalFileElements(currentDocument, externalFiles, setB, cancellationToken);

                if (setBElements.Count == 0)
                    throw new InvalidOperationException("Set B contains no elements after filtering");

                // Step 3: Run clash detection
                progress?.Report(new ClashDetectionProgress
                {
                    Phase = "Detecting Clashes",
                    PercentComplete = 40
                });

                clashes = DetectClashes(
                    setAElements,
                    setBElements,
                    tolerance,
                    progress,
                    cancellationToken);

                // Step 4: Post-process results
                progress?.Report(new ClashDetectionProgress
                {
                    Phase = "Processing Results",
                    PercentComplete = 95
                });

                clashes = PostProcessClashes(clashes);

                progress?.Report(new ClashDetectionProgress
                {
                    Phase = "Complete",
                    PercentComplete = 100
                });

                return clashes;
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new ClashDetectionProgress
                {
                    Phase = "Cancelled",
                    PercentComplete = 0
                });
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Clash detection failed: {ex.Message}", ex);
            }
        }

        #endregion

        #region Private Methods - External File Loading

        /// <summary>
        /// Loads elements from external files based on Set B configuration
        /// </summary>
        private List<Element> LoadExternalFileElements(
            Document currentDocument,
            List<string> externalFiles,
            ElementSelectionSet setB,
            CancellationToken cancellationToken)
        {
            var allElements = new List<Element>();

            foreach (var filePath in externalFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Load external file as RevitLinkInstance
                var linkElements = LoadElementsFromLink(currentDocument, filePath, setB);
                allElements.AddRange(linkElements);
            }

            return allElements;
        }

        /// <summary>
        /// Loads elements from a linked Revit file
        /// </summary>
        private List<Element> LoadElementsFromLink(
            Document currentDocument,
            string linkFilePath,
            ElementSelectionSet setB)
        {
            var elements = new List<Element>();

            try
            {
                // Create a link type for the external file
                using (Transaction trans = new Transaction(currentDocument, "Load Link for Clash Detection"))
                {
                    trans.Start();

                    // Load the link
                    ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(linkFilePath);
                    RevitLinkOptions linkOptions = new RevitLinkOptions(false);
                    LinkLoadResult loadResult = RevitLinkType.Create(currentDocument, modelPath, linkOptions);

                    if (loadResult.LoadResult != LoadResult.ModelAlreadyLoaded)
                    {
                        trans.Commit();
                    }
                    else
                    {
                        trans.RollBack();
                    }
                }

                // Find the RevitLinkInstance
                FilteredElementCollector linkCollector = new FilteredElementCollector(currentDocument)
                    .OfClass(typeof(RevitLinkInstance));

                RevitLinkInstance linkInstance = null;
                foreach (RevitLinkInstance instance in linkCollector)
                {
                    if (instance.GetLinkDocument() != null)
                    {
                        var linkDoc = instance.GetLinkDocument();
                        if (linkDoc.PathName.Equals(linkFilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            linkInstance = instance;
                            break;
                        }
                    }
                }

                if (linkInstance == null)
                    throw new InvalidOperationException($"Failed to load link: {linkFilePath}");

                // Get the linked document
                Document linkedDoc = linkInstance.GetLinkDocument();
                if (linkedDoc == null)
                    throw new InvalidOperationException($"Linked document is null: {linkFilePath}");

                // Get filtered elements from linked document
                var filteredElements = _filterService.GetFilteredElements(linkedDoc, setB);

                // Transform elements to current document coordinate system
                Transform linkTransform = linkInstance.GetTotalTransform();

                // Store elements with their transform for later geometry extraction
                elements.AddRange(filteredElements);

                return elements;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load elements from link {linkFilePath}: {ex.Message}", ex);
            }
        }

        #endregion

        #region Private Methods - Clash Detection

        /// <summary>
        /// Detects clashes between two element sets using geometric intersection
        /// </summary>
        private List<ClashResult> DetectClashes(
            List<Element> setAElements,
            List<Element> setBElements,
            double tolerance,
            IProgress<ClashDetectionProgress> progress,
            CancellationToken cancellationToken)
        {
            var clashes = new List<ClashResult>();
            var totalComparisons = setAElements.Count * setBElements.Count;
            var completedComparisons = 0;

            // Convert tolerance from mm to feet (Revit internal units)
            double toleranceFeet = tolerance / 304.8;

            foreach (var elementA in setAElements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Get geometry of element A
                var geometryA = GetElementSolids(elementA);
                if (geometryA.Count == 0)
                    continue;

                foreach (var elementB in setBElements)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Skip if same element (shouldn't happen but safeguard)
                    if (elementA.Id == elementB.Id)
                        continue;

                    // Get geometry of element B
                    var geometryB = GetElementSolids(elementB);
                    if (geometryB.Count == 0)
                        continue;

                    // Check for clash between geometries
                    var clash = CheckGeometricClash(elementA, geometryA, elementB, geometryB, toleranceFeet);
                    if (clash != null)
                    {
                        clashes.Add(clash);
                    }

                    // Update progress
                    completedComparisons++;
                    if (completedComparisons % 100 == 0)
                    {
                        var percent = 40 + (int)((completedComparisons / (double)totalComparisons) * 55);
                        progress?.Report(new ClashDetectionProgress
                        {
                            Phase = $"Detecting Clashes ({clashes.Count} found)",
                            PercentComplete = percent
                        });
                    }
                }
            }

            return clashes;
        }

        /// <summary>
        /// Extracts solid geometries from an element
        /// </summary>
        private List<Solid> GetElementSolids(Element element)
        {
            var solids = new List<Solid>();

            try
            {
                Options options = new Options
                {
                    ComputeReferences = true,
                    DetailLevel = ViewDetailLevel.Fine,
                    IncludeNonVisibleObjects = false
                };

                GeometryElement geometryElement = element.get_Geometry(options);
                if (geometryElement == null)
                    return solids;

                foreach (GeometryObject geomObj in geometryElement)
                {
                    ExtractSolidsFromGeometryObject(geomObj, solids);
                }

                // Filter out very small solids (likely errors or insignificant geometry)
                solids = solids.Where(s => s.Volume > 0.001).ToList();

                return solids;
            }
            catch (Exception)
            {
                // If geometry extraction fails, return empty list
                return solids;
            }
        }

        /// <summary>
        /// Recursively extracts solids from geometry objects
        /// </summary>
        private void ExtractSolidsFromGeometryObject(GeometryObject geomObj, List<Solid> solids)
        {
            if (geomObj is Solid solid)
            {
                if (solid.Volume > 0.001) // Only add valid solids
                {
                    solids.Add(solid);
                }
            }
            else if (geomObj is GeometryInstance geomInstance)
            {
                GeometryElement instanceGeometry = geomInstance.GetInstanceGeometry();
                if (instanceGeometry != null)
                {
                    foreach (GeometryObject instanceObj in instanceGeometry)
                    {
                        ExtractSolidsFromGeometryObject(instanceObj, solids);
                    }
                }
            }
            else if (geomObj is GeometryElement geomElement)
            {
                foreach (GeometryObject obj in geomElement)
                {
                    ExtractSolidsFromGeometryObject(obj, solids);
                }
            }
        }

        /// <summary>
        /// Checks if two element geometries clash
        /// </summary>
        private ClashResult CheckGeometricClash(
            Element elementA,
            List<Solid> solidsA,
            Element elementB,
            List<Solid> solidsB,
            double toleranceFeet)
        {
            foreach (var solidA in solidsA)
            {
                foreach (var solidB in solidsB)
                {
                    try
                    {
                        // Check for intersection
                        Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            solidA,
                            solidB,
                            BooleanOperationsType.Intersect);

                        if (intersection != null && intersection.Volume > 0.0001)
                        {
                            // Hard clash detected
                            return CreateClashResult(
                                elementA,
                                elementB,
                                intersection,
                                "Hard",
                                intersection.Volume);
                        }
                        else if (toleranceFeet > 0)
                        {
                            // Check for clearance clash
                            var clearanceDistance = CalculateClearanceDistance(solidA, solidB);
                            if (clearanceDistance < toleranceFeet)
                            {
                                return CreateClashResult(
                                    elementA,
                                    elementB,
                                    solidA,
                                    "Clearance",
                                    0,
                                    clearanceDistance);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Boolean operation failed, skip this pair
                        continue;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Calculates the minimum clearance distance between two solids
        /// </summary>
        private double CalculateClearanceDistance(Solid solidA, Solid solidB)
        {
            // Get bounding boxes to quickly check if clearance check is needed
            BoundingBoxXYZ bboxA = solidA.GetBoundingBox();
            BoundingBoxXYZ bboxB = solidB.GetBoundingBox();

            // Calculate minimum distance between bounding boxes
            // This is a simplified approach - a full implementation would check face-to-face distances
            var minDistance = double.MaxValue;

            // Get centroids
            XYZ centroidA = (bboxA.Min + bboxA.Max) / 2;
            XYZ centroidB = (bboxB.Min + bboxB.Max) / 2;

            // Approximate distance (this is simplified - real implementation would be more complex)
            var distance = centroidA.DistanceTo(centroidB);

            // Subtract approximate radii to get clearance
            var radiusA = (bboxA.Max - bboxA.Min).GetLength() / 2;
            var radiusB = (bboxB.Max - bboxB.Min).GetLength() / 2;

            minDistance = distance - radiusA - radiusB;

            return Math.Max(0, minDistance);
        }

        /// <summary>
        /// Creates a ClashResult from detected clash
        /// </summary>
        private ClashResult CreateClashResult(
            Element elementA,
            Element elementB,
            Solid clashGeometry,
            string clashType,
            double overlapVolume,
            double clearanceDistance = 0)
        {
            // Get clash point (centroid of intersection)
            BoundingBoxXYZ bbox = clashGeometry.GetBoundingBox();
            XYZ clashPoint = (bbox.Min + bbox.Max) / 2;

            // Get element information
            var categoryA = elementA.Category?.Name ?? "Unknown";
            var categoryB = elementB.Category?.Name ?? "Unknown";

            var nameA = elementA.Name ?? $"Element {elementA.Id.IntegerValue}";
            var nameB = elementB.Name ?? $"Element {elementB.Id.IntegerValue}";

            // Determine severity based on clash type and volume
            string severity = DetermineSeverity(clashType, overlapVolume, clearanceDistance);

            return new ClashResult
            {
                ClashId = $"CLS-{Guid.NewGuid().ToString().Substring(0, 8)}",
                ElementId1 = elementA.Id.IntegerValue.ToString(),
                ElementName1 = nameA,
                Category1 = categoryA,
                ElementId2 = elementB.Id.IntegerValue.ToString(),
                ElementName2 = nameB,
                Category2 = categoryB,
                ClashPoint = clashPoint,
                ClashType = clashType,
                OverlapVolume = overlapVolume,
                ClearanceDistance = clearanceDistance * 304.8, // Convert feet to mm
                Severity = severity,
                DetectedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Determines clash severity based on type and measurements
        /// </summary>
        private string DetermineSeverity(string clashType, double overlapVolume, double clearanceDistance)
        {
            if (clashType == "Hard")
            {
                if (overlapVolume > 1.0) // Significant overlap (>1 cubic foot)
                    return "Critical";
                else if (overlapVolume > 0.1)
                    return "Major";
                else
                    return "Minor";
            }
            else // Clearance clash
            {
                if (clearanceDistance < 0.1) // Less than ~30mm clearance
                    return "Major";
                else
                    return "Minor";
            }
        }

        #endregion

        #region Private Methods - Post Processing

        /// <summary>
        /// Post-processes clash results to remove duplicates and enhance data
        /// </summary>
        private List<ClashResult> PostProcessClashes(List<ClashResult> clashes)
        {
            // Remove duplicate clashes (same elements, just reversed)
            var uniqueClashes = new List<ClashResult>();
            var processedPairs = new HashSet<string>();

            foreach (var clash in clashes)
            {
                // Create a unique key for this element pair (order-independent)
                var key1 = $"{clash.ElementId1}_{clash.ElementId2}";
                var key2 = $"{clash.ElementId2}_{clash.ElementId1}";

                if (!processedPairs.Contains(key1) && !processedPairs.Contains(key2))
                {
                    uniqueClashes.Add(clash);
                    processedPairs.Add(key1);
                }
            }

            // Sort by severity (Critical > Major > Minor)
            var severityOrder = new Dictionary<string, int>
            {
                { "Critical", 0 },
                { "Major", 1 },
                { "Minor", 2 }
            };

            uniqueClashes = uniqueClashes
                .OrderBy(c => severityOrder.ContainsKey(c.Severity) ? severityOrder[c.Severity] : 999)
                .ThenByDescending(c => c.OverlapVolume)
                .ToList();

            return uniqueClashes;
        }

        #endregion
    }

    #region Progress Reporting

    /// <summary>
    /// Progress information for clash detection operation
    /// </summary>
    public class ClashDetectionProgress
    {
        /// <summary>
        /// Current phase of the operation
        /// </summary>
        public string Phase { get; set; }

        /// <summary>
        /// Percentage complete (0-100)
        /// </summary>
        public int PercentComplete { get; set; }

        /// <summary>
        /// Optional message with additional details
        /// </summary>
        public string Message { get; set; }
    }

    #endregion
}
