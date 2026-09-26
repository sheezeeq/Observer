namespace Observer;

internal static class DetectionFusion
{
    public static IReadOnlyList<Detection> Match(
        IReadOnlyList<Detection> motion,
        IReadOnlyList<VehicleBox> vehicles,
        bool includeUnknown)
    {
        var found = new List<Detection>();
        foreach (var item in motion)
        {
            double area = item.Bounds.Width * (double)item.Bounds.Height;
            if (area <= 0) continue;
            var center = new Point(item.Bounds.Left + item.Bounds.Width / 2,
                item.Bounds.Top + item.Bounds.Height / 2);
            var match = vehicles
                .Where(vehicle => vehicle.Bounds.Contains(center))
                .Select(vehicle => (Vehicle: vehicle, Overlap: Rectangle.Intersect(vehicle.Bounds, item.Bounds)))
                .Where(candidate => candidate.Overlap.Width * (double)candidate.Overlap.Height >= area * 0.5)
                .OrderByDescending(candidate => candidate.Vehicle.Confidence)
                .FirstOrDefault();
            if (match.Vehicle is not null)
            {
                if (!found.Any(x => x.Label is not null && x.Bounds == match.Vehicle.Bounds))
                    found.Add(new Detection(match.Vehicle.Bounds, item.Pixels,
                        match.Vehicle.Label, match.Vehicle.Confidence));
            }
            else if (includeUnknown)
            {
                found.Add(item);
            }
        }
        return found;
    }
}
