using System.Text;
using System.Text.RegularExpressions;

public class PatientEvent
{
    public PatientEvent(string id, DateTime date, string sctTotal)
    {
        Id = id;
        Date = date;
        SctTotal = sctTotal;
    }
    public string Id { get; }
    public DateTime Date { get; }
    public string SctTotal { get; }
    
}

public class PostCoordinatedExpression {
    public PostCoordinatedExpression(string expression, string description) {
      Expression = expression;
      Description = description;
    }

    public string Expression {get;}
    public string Description {get;}

    public List<ProcedureSite> ProcedureSites {get;} = new List<ProcedureSite>();

    internal string GenerateEclForDentalCaries()
    {
        ProcedureSite procedureSiteForTooth = ProcedureSites.FirstOrDefault(p => p.IsTooth);
        if (procedureSiteForTooth == null)
        {
            Console.WriteLine("Missing Tooth procedure site - unable to create ECL!");
            throw new Exception("Missing tooth!");
        }
        
        var builder = new StringBuilder("<<80967001 |Dental caries|:");
        builder.Append("363698007 |Finding site|=").Append(procedureSiteForTooth.Concept.Code);

        // list of surface sct ids
        var surfaces = ProcedureSites.Where(p => p.IsSurface).Select(p => p.Concept.Code);

        builder.Append(",363698007 |Finding site|=(");
        builder.Append(string.Join(" OR ", surfaces));
        builder.Append(")");
        
        return builder.ToString();
    }

    internal List<ProcedureSite> ResolveProcedureSites(Dictionary<string, SctConcept> teeth, Dictionary<string, SctConcept> surfaces)
    {
      // empty any old values
      ProcedureSites.Clear();

      string pattern = "363704007=([0-9]+)";
      MatchCollection matches = Regex.Matches(Expression, pattern);

      // Use foreach-loop.
      foreach (Match match in matches)
      {
        GroupCollection groups = match.Groups;
        string matchValue = groups[1].Value;
        // check if procedure site is in a thooth
        if (teeth.ContainsKey(matchValue))
        {
          //           Console.WriteLine($"Found procedure site in tooth {groups[1]}");
          ProcedureSites.Add(new ProcedureSite(teeth[matchValue], true, false));
        }
        if (surfaces.ContainsKey(matchValue))
        {
          //           Console.WriteLine($"Found procedure site in tooth {groups[1]}");
          ProcedureSites.Add(new ProcedureSite(surfaces[matchValue], false, true));
        }
      }
      return ProcedureSites;
    }

    public void PrintProcedureSitesToConsole()
    {
        foreach (var site in ProcedureSites)
        {
            Console.Write("* ");
            if (site.IsSurface)
                Console.Write("SURFACE ");
            if (site.IsTooth)
                Console.Write("TOOTH ");
            Console.Write(site.Concept.Code);
            Console.WriteLine(" | " + site.Concept.Description + " | ");
        }
    }
}

public class SctConcept {
    public SctConcept(string code, string description) {
        this.Code = code;
        this.Description = description;
    }

    public string Code {get;set;}
    public string Description {get;set;}
}


public class ProcedureSite {

    public ProcedureSite(SctConcept concept, bool isTooth, bool isSurface) {
        Concept = concept;
        IsTooth = isTooth;
        IsSurface = isSurface;
    }

    public SctConcept Concept {get;set;}
    public bool IsTooth {get;set;}
    public bool IsSurface {get;set;}
}

public class FhirValueSetResponse
{
    public Expansion? Expansion { get; set; }
}

public class Expansion
{
    public List<Contains>? Contains { get; set; }
}

public class Contains
{
    public string? Code { get; set; }
    public string? Display {get;set;}
    public string? System {get;set;}
}

public class SCTinstance
{
    public string? InitAnoPID { get; set; }
    public DateTime InitDate { get; set; }
    public string? InitSCTtotal { get; set; }
}