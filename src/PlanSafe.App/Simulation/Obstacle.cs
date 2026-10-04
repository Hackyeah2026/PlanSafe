namespace PlanSafe.App.Simulation;

public class Obstacle
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? Id { get; set; }
    public Obstacle(double x, double y, double width, double height, string? id = null) => (X, Y, Width, Height, Id) = (x, y, width, height, id);
}
