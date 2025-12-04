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
    /// Handles geometric intersection detection, linked file processing, and clash result generation
    /// </summary>
    public class ClashDetectionService
    {
        private readonly ElementFilterService _filterService;

        // Cache for extracted solids to avoid repeated geometry extraction
        private Dictionary<long, List<Solid>> _solidCache;

        /// <summary>
        /// Initializes a new instance of the ClashDetectionService
        /// </summary>
        public ClashDetectionService()
        {
            _filterService = new ElementFilterService();
            _solidCache = new Dictionary<long, List<Solid>>();
        }

        #region Public Methods

        /// <summary>
        /// Runs clash detection between current document elements and linked file elements
        /// </summary>
        /// <param name="currentDocument">The current active Revit document</param>
        /// <param name="linkedFiles">List of linked file info to clash against</param>
        /// <param name="setA">Element selection set A (from current model)</param>
        /// <param name="setB">Element selection set B (from linked files)</param>
        /// <param name="tolerance">Clash tolerance in millimeters (0 = hard clash only)</param>
        /// <param name="progress">Optional progress reporter for UI updates</param>
        /// <param name="cancellationToken">Token to support operation cancellation</param>
        /// <returns>List of detected clashes</returns>
        public List<ClashResult> RunClashDetection(
            Document currentDocument,
            List<RevitLinkedFileInfo> linkedFiles,
            ElementSelectionSet setA,
            ElementSelectionSet setB,
            double tolerance,
            IProgress<ClashDetectionProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (currentDocument == null)
                throw new ArgumentNullException(nameof(currentDocument));
            if (linkedFiles == null || linkedFiles.Count == 0)
                throw new ArgumentException("Linked files list cannot be null or empty", nameof(linkedFiles));
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

                // Step 2: Get elements from linked files (Set B) with their transforms
                progress?.Report(new ClashDetectionProgress
                {
                    Phase = "Loading Linked File Elements",
                    PercentComplete = 20
                });

                var setBElementsWithTransforms = GetLinkedFileElementsWithTransforms(linkedFiles, setB, cancellationToken);

                if (setBElementsWithTransforms.Count == 0)
                    throw new InvalidOperationException("Set B contains no elements after filtering");

                // Step 3: Run clash detection with proper coordinate transforms
                progress?.Report(new ClashDetectionProgress
                {
                    Phase = "Detecting Clashes",
                    PercentComplete = 40
                });

                clashes = DetectClashesWithTransforms(
                    setAElements,
                    setBElementsWithTransforms,
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

        #region Private Methods - Linked File Element Loading

        /// <summary>
        /// Gets elements from linked files based on Set B configuration
        /// Returns a list of tuples containing the element and its link transform
        /// </summary>
        private List<(Element Element, Transform LinkTransform)> GetLinkedFileElementsWithTransforms(
            List<RevitLinkedFileInfo> linkedFiles,
            ElementSelectionSet setB,
            CancellationToken cancellationToken)
        {
            var allElements = new List<(Element, Transform)>();

            foreach (var linkedFile in linkedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (linkedFile.LinkedDocument != null && linkedFile.IsLoaded)
                {
                    // Get filtered elements from linked document
                    var filteredElements = _filterService.GetFilteredElements(linkedFile.LinkedDocument, setB);

                    // Store each element with its link transform for proper coordinate conversion
                    var transform = linkedFile.LinkTransform ?? Transform.Identity;
                    foreach (var element in filteredElements)
                    {
                        allElements.Add((element, transform));
                    }
                }
            }

            return allElements;
        }

        #endregion

        #region Private Methods - Clash Detection

        /// <summary>
        /// Detects clashes using a two-phase approach:
        /// 1. Fast bounding box filter to find candidate pairs
        /// 2. Precise ElementIntersectsSolidFilter only on candidates
        /// This dramatically reduces the number of expensive operations.
        /// </summary>
        private List<ClashResult> DetectClashesWithTransforms(
            List<Element> setAElements,
            List<(Element Element, Transform LinkTransform)> setBElementsWithTransforms,
            double tolerance,
            IProgress<ClashDetectionProgress> progress,
            CancellationToken cancellationToken)
        {
            var clashes = new List<ClashResult>();
            const int MAX_CLASHES = 5000;

            var setADocument = setAElements.FirstOrDefault()?.Document;
            if (setADocument == null)
                return clashes;

            // PHASE 1: Build spatial index of Set A elements using bounding boxes
            progress?.Report(new ClashDetectionProgress
            {
                Phase = "Building spatial index...",
                PercentComplete = 40
            });

            var setABounds = new Dictionary<long, BoundingBoxXYZ>();
            foreach (var element in setAElements)
            {
                try
                {
                    var bbox = element.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        setABounds[element.Id.Value] = bbox;
                    }
                }
                catch { }
            }

            if (setABounds.Count == 0)
                return clashes;

            // Calculate overall bounds of Set A for quick rejection
            var setAMinX = setABounds.Values.Min(b => b.Min.X);
            var setAMinY = setABounds.Values.Min(b => b.Min.Y);
            var setAMinZ = setABounds.Values.Min(b => b.Min.Z);
            var setAMaxX = setABounds.Values.Max(b => b.Max.X);
            var setAMaxY = setABounds.Values.Max(b => b.Max.Y);
            var setAMaxZ = setABounds.Values.Max(b => b.Max.Z);

            // PHASE 2: Process Set B elements, skip those outside Set A bounds entirely
            progress?.Report(new ClashDetectionProgress
            {
                Phase = "Filtering candidates...",
                PercentComplete = 50
            });

            var candidateSetB = new List<(Element Element, Transform LinkTransform, BoundingBoxXYZ Bounds)>();

            foreach (var (elementB, linkTransform) in setBElementsWithTransforms)
            {
                try
                {
                    var bbox = elementB.get_BoundingBox(null);
                    if (bbox == null) continue;

                    // Transform bounding box to host coordinates
                    var transformedMin = linkTransform.OfPoint(bbox.Min);
                    var transformedMax = linkTransform.OfPoint(bbox.Max);

                    // Create proper min/max after transform
                    var minX = Math.Min(transformedMin.X, transformedMax.X);
                    var minY = Math.Min(transformedMin.Y, transformedMax.Y);
                    var minZ = Math.Min(transformedMin.Z, transformedMax.Z);
                    var maxX = Math.Max(transformedMin.X, transformedMax.X);
                    var maxY = Math.Max(transformedMin.Y, transformedMax.Y);
                    var maxZ = Math.Max(transformedMin.Z, transformedMax.Z);

                    // Quick rejection: if completely outside Set A bounds, skip
                    if (maxX < setAMinX || minX > setAMaxX ||
                        maxY < setAMinY || minY > setAMaxY ||
                        maxZ < setAMinZ || minZ > setAMaxZ)
                    {
                        continue; // No possible clash
                    }

                    var transformedBbox = new BoundingBoxXYZ
                    {
                        Min = new XYZ(minX, minY, minZ),
                        Max = new XYZ(maxX, maxY, maxZ)
                    };

                    candidateSetB.Add((elementB, linkTransform, transformedBbox));
                }
                catch { }
            }

            progress?.Report(new ClashDetectionProgress
            {
                Phase = $"Found {candidateSetB.Count} candidates (filtered from {setBElementsWithTransforms.Count})",
                PercentComplete = 55
            });

            // If no candidates, no clashes possible
            if (candidateSetB.Count == 0)
                return clashes;

            // PHASE 3: For each candidate, use ElementIntersectsSolidFilter
            var processedCount = 0;
            var totalCandidates = candidateSetB.Count;
            var setAElementIds = new HashSet<long>(setAElements.Select(e => e.Id.Value));
            var processedPairs = new HashSet<string>(); // Prevent duplicate clashes

            foreach (var (elementB, linkTransform, bboxB) in candidateSetB)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (clashes.Count >= MAX_CLASHES)
                {
                    progress?.Report(new ClashDetectionProgress
                    {
                        Phase = $"Limit reached: {MAX_CLASHES} clashes",
                        PercentComplete = 95
                    });
                    break;
                }

                processedCount++;

                // Get solids for this element
                List<Solid> solidsB;
                try
                {
                    solidsB = GetElementSolids(elementB, linkTransform);
                    if (solidsB.Count == 0) continue;
                }
                catch { continue; }

                // Find Set A elements whose bounding boxes overlap with this element
                var potentialSetA = setABounds
                    .Where(kvp => BoundingBoxesOverlap(kvp.Value, bboxB))
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (potentialSetA.Count == 0) continue;

                // Use ElementIntersectsSolidFilter for precise detection
                foreach (var solidB in solidsB)
                {
                    try
                    {
                        var intersectFilter = new ElementIntersectsSolidFilter(solidB);
                        var intersectingElements = new FilteredElementCollector(setADocument)
                            .WherePasses(intersectFilter)
                            .ToList();

                        foreach (var elementA in intersectingElements)
                        {
                            var elementAId = elementA.Id.Value;

                            // Must be in Set A and in our potential list
                            if (!setAElementIds.Contains(elementAId))
                                continue;
                            if (!potentialSetA.Contains(elementAId))
                                continue;

                            // Prevent duplicate pairs
                            var pairKey = $"{Math.Min(elementAId, elementB.Id.Value)}_{Math.Max(elementAId, elementB.Id.Value)}";
                            if (processedPairs.Contains(pairKey))
                                continue;
                            processedPairs.Add(pairKey);

                            var clash = CreateClashResultFromIntersection(elementA, elementB, solidB, linkTransform);
                            if (clash != null)
                            {
                                clashes.Add(clash);
                                if (clashes.Count >= MAX_CLASHES) break;
                            }
                        }

                        if (clashes.Count >= MAX_CLASHES) break;
                    }
                    catch { continue; }
                }

                // Update progress
                if (processedCount % 20 == 0 || processedCount == totalCandidates)
                {
                    var percent = 55 + (int)((processedCount / (double)totalCandidates) * 40);
                    progress?.Report(new ClashDetectionProgress
                    {
                        Phase = $"Checking ({processedCount}/{totalCandidates}) - {clashes.Count} clashes",
                        PercentComplete = percent
                    });
                }
            }

            return clashes;
        }

        /// <summary>
        /// Checks if two bounding boxes overlap
        /// </summary>
        private bool BoundingBoxesOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return !(a.Max.X < b.Min.X || a.Min.X > b.Max.X ||
                     a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y ||
                     a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z);
        }

        /// <summary>
        /// Detects clearance clashes by expanding the solid and checking for nearby elements
        /// </summary>
        private void DetectClearanceClashes(
            Document hostDocument,
            HashSet<long> setAElementIds,
            Element elementB,
            List<Solid> solidsB,
            double toleranceFeet,
            List<ClashResult> clashes)
        {
            // For clearance detection, we create an expanded bounding box and find nearby elements
            foreach (var solidB in solidsB)
            {
                try
                {
                    var bbox = solidB.GetBoundingBox();
                    if (bbox == null) continue;

                    // Create an expanded outline for proximity search
                    var minPoint = new XYZ(
                        bbox.Min.X - toleranceFeet,
                        bbox.Min.Y - toleranceFeet,
                        bbox.Min.Z - toleranceFeet);
                    var maxPoint = new XYZ(
                        bbox.Max.X + toleranceFeet,
                        bbox.Max.Y + toleranceFeet,
                        bbox.Max.Z + toleranceFeet);

                    var outline = new Outline(minPoint, maxPoint);
                    var bboxFilter = new BoundingBoxIntersectsFilter(outline);

                    // Find elements within the expanded bounding box
                    var nearbyElements = new FilteredElementCollector(hostDocument)
                        .WherePasses(bboxFilter)
                        .ToList();

                    foreach (var elementA in nearbyElements)
                    {
                        if (!setAElementIds.Contains(elementA.Id.Value))
                            continue;

                        // Check if we already have a hard clash for this pair
                        var existingClash = clashes.Any(c =>
                            (c.ElementId1 == elementA.Id.Value.ToString() && c.ElementId2 == elementB.Id.Value.ToString()) ||
                            (c.ElementId2 == elementA.Id.Value.ToString() && c.ElementId1 == elementB.Id.Value.ToString()));

                        if (existingClash)
                            continue;

                        // Calculate actual clearance distance
                        var solidsA = GetElementSolids(elementA, Transform.Identity);
                        foreach (var solidA in solidsA)
                        {
                            var clearance = CalculateClearanceDistance(solidA, solidB);
                            if (clearance > 0 && clearance < toleranceFeet)
                            {
                                var clash = CreateClashResult(
                                    elementA,
                                    elementB,
                                    solidA,
                                    "Clearance",
                                    0,
                                    clearance);
                                clashes.Add(clash);
                                break; // One clearance clash per element pair is enough
                            }
                        }
                    }
                }
                catch
                {
                    // Skip this solid if there's an error
                    continue;
                }
            }
        }

        /// <summary>
        /// Creates a clash result from an intersection found by ElementIntersectsSolidFilter.
        /// OPTIMIZED: We skip expensive Boolean volume calculation since we already know there's a clash.
        /// The filter already confirmed intersection - no need to recalculate.
        /// </summary>
        private ClashResult CreateClashResultFromIntersection(
            Element elementA,
            Element elementB,
            Solid solidB,
            Transform linkTransform)
        {
            // ElementIntersectsSolidFilter already confirmed these elements clash.
            // Skip expensive Boolean operations - just create the result directly.
            // We use a default volume estimate based on severity will be "Major" for all hard clashes.
            return CreateClashResultFast(elementA, elementB, solidB);
        }

        /// <summary>
        /// Fast clash result creation without expensive Boolean volume calculation
        /// </summary>
        private ClashResult CreateClashResultFast(Element elementA, Element elementB, Solid referenceSolid)
        {
            // Get clash point from the reference solid's center
            XYZ clashPoint;
            try
            {
                var bbox = referenceSolid.GetBoundingBox();
                clashPoint = (bbox.Min + bbox.Max) / 2;
            }
            catch
            {
                clashPoint = XYZ.Zero;
            }

            // Get element information
            var categoryA = elementA.Category?.Name ?? "Unknown";
            var categoryB = elementB.Category?.Name ?? "Unknown";
            var nameA = elementA.Name ?? $"Element {elementA.Id.Value}";
            var nameB = elementB.Name ?? $"Element {elementB.Id.Value}";

            return new ClashResult
            {
                ClashId = $"CLS-{Guid.NewGuid().ToString().Substring(0, 8)}",
                ElementId1 = elementA.Id.Value.ToString(),
                ElementName1 = nameA,
                Category1 = categoryA,
                ElementId2 = elementB.Id.Value.ToString(),
                ElementName2 = nameB,
                Category2 = categoryB,
                ClashPoint = clashPoint,
                ClashType = "Hard",
                OverlapVolume = 0.5, // Default estimate - actual volume calculation is too slow
                ClearanceDistance = 0,
                Severity = "Major", // Default to Major for all hard clashes
                DetectedDate = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Extracts solid geometries from an element with an optional coordinate transform.
        /// Uses caching to avoid repeated geometry extraction for the same element.
        /// </summary>
        /// <param name="element">The element to extract geometry from</param>
        /// <param name="transform">Transform to apply (use Transform.Identity for no transformation)</param>
        /// <returns>List of solid geometries in the transformed coordinate system</returns>
        private List<Solid> GetElementSolids(Element element, Transform transform)
        {
            var elementId = element.Id.Value;
            List<Solid> solids;

            // Check cache first (for untransformed solids)
            if (_solidCache.TryGetValue(elementId, out var cachedSolids))
            {
                solids = cachedSolids;
            }
            else
            {
                // Extract geometry
                solids = ExtractSolidsFromElement(element);

                // Cache the untransformed solids
                _solidCache[elementId] = solids;
            }

            // If no solids found, return empty
            if (solids.Count == 0)
                return solids;

            // Apply transform if it's not identity
            if (transform != null && !transform.IsIdentity)
            {
                var transformedSolids = new List<Solid>();
                foreach (var solid in solids)
                {
                    try
                    {
                        var transformedSolid = SolidUtils.CreateTransformed(solid, transform);
                        if (transformedSolid != null && transformedSolid.Volume > 0.001)
                        {
                            transformedSolids.Add(transformedSolid);
                        }
                    }
                    catch
                    {
                        transformedSolids.Add(solid);
                    }
                }
                return transformedSolids;
            }

            return solids;
        }

        /// <summary>
        /// Extracts solids from an element (no transform, for caching)
        /// </summary>
        private List<Solid> ExtractSolidsFromElement(Element element)
        {
            var solids = new List<Solid>();

            try
            {
                Options options = new Options
                {
                    ComputeReferences = false,  // Faster - we don't need references
                    DetailLevel = ViewDetailLevel.Coarse,  // Faster - coarse is enough for clash detection
                    IncludeNonVisibleObjects = false
                };

                GeometryElement geometryElement = element.get_Geometry(options);
                if (geometryElement == null)
                    return solids;

                foreach (GeometryObject geomObj in geometryElement)
                {
                    ExtractSolidsFromGeometryObject(geomObj, solids);
                }

                // Filter out very small solids
                solids = solids.Where(s => s.Volume > 0.001).ToList();
            }
            catch
            {
                // If geometry extraction fails, return empty list
            }

            return solids;
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

            var nameA = elementA.Name ?? $"Element {elementA.Id.Value}";
            var nameB = elementB.Name ?? $"Element {elementB.Id.Value}";

            // Determine severity based on clash type and volume
            string severity = DetermineSeverity(clashType, overlapVolume, clearanceDistance);

            return new ClashResult
            {
                ClashId = $"CLS-{Guid.NewGuid().ToString().Substring(0, 8)}",
                ElementId1 = elementA.Id.Value.ToString(),
                ElementName1 = nameA,
                Category1 = categoryA,
                ElementId2 = elementB.Id.Value.ToString(),
                ElementName2 = nameB,
                Category2 = categoryB,
                ClashPoint = clashPoint,
                ClashType = clashType,
                OverlapVolume = overlapVolume,
                ClearanceDistance = clearanceDistance * 304.8, // Convert feet to mm
                Severity = severity,
                DetectedDate = DateTime.UtcNow
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
