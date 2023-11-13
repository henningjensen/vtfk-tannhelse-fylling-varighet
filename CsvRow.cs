using CsvHelper.Configuration.Attributes;

[Delimiter(";")]
public class CsvRow
{
    [Name("AnoPID")]
    public string PatientId { get; set; }
    
    [Format("yyyy-MM-dd")]
    public DateTime InitDate { get; set; }
    
    public string InitPce { get; set; }
    
    [Format("yyyy-MM-dd")]
    public DateTime? EventDate { get; set; }
    
    public string? EventPce { get; set; }

    [Format("yyyy-MM-dd")]
    public DateTime? LastExaminationDate { get; set; }
}