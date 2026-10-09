namespace Blackbird.Models;

public class SetupCheck
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Passed { get; set; }
    public bool IsRequired { get; set; } = true;

    public bool IsMissingRequired => !Passed && IsRequired;
    public bool IsMissingOptional => !Passed && !IsRequired;

    public string StatusText => Passed ? "Found" : IsRequired ? "Missing" : "Not installed (optional)";
}
