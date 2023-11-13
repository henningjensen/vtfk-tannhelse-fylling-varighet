using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Flurl;
using Flurl.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public class Program
{
    List<PostCoordinatedExpression> PostCoordinatedExpressionInitial;
    Dictionary<string, SctConcept> Surfaces;
    Dictionary<string, SctConcept> Teeth;
    List<SCTinstance> SCTinstances;

    private IConfiguration Config;
    bool Debug = false;

    public static async Task Main(string[] args)
    {
        await new Program().Run(args);
    }
 
    /// <summary>
    /// use ODBC as default database connection
    /// </summary>
    /// <returns></returns>
    DbConnection GetDbConnection()
    {
        return new OdbcConnection("DSN=Rdata");
    }

    public async Task Run(string[] args)
    {
        // read config from appsettings.json
        var host = Host.CreateDefaultBuilder().Build();
        Config = host.Services.GetRequiredService<IConfiguration>();

        try
        {
            PostCoordinatedExpressionInitial = await GetPostCoordinatedExpressions();
            Surfaces = await GetSurfaces();
            Teeth = await GetTeeth();

            await LookupDentalRestorationForPatients();
        }
        catch (FlurlHttpException ex)
        {
            var error = await ex.GetResponseStringAsync();
            Console.WriteLine($"Error returned from {ex.Call.Request.Url}: {error}");
        }
    }


    async Task LookupDentalRestorationForPatients()
    {
        // save all durations for statistics
        HashSet<double> duration = new HashSet<double>();
        
        if (Debug)
            Console.WriteLine("AnoPID;initDate;initPCE;eventDate;eventPCE;lastExamDate");

        using (var connection = GetDbConnection())
        {
            connection.Open();

            Dictionary<string, DateTime> lastExaminationDateForPatients =
                FindLastExaminationDateForPatients(connection);

            var command = connection.CreateCommand();
            command.CommandText =
                @"
        SELECT AnoPID,Date,SCTtotal
        FROM PcJSON
        WHERE SCTtotal IN(" + string.Join(",",
                                Enumerable.Range(0, PostCoordinatedExpressionInitial.Count).Select(z => "?"))
                            + ") ORDER BY AnoPID, Date";
            
            for (var i = 0; i < PostCoordinatedExpressionInitial.Count; i++)
            {
                // the named parameter is actually not in use in ODBC, but is required in the api for creating a new parameter
                var odbcParameter = new OdbcParameter("@param" + i, PostCoordinatedExpressionInitial[i].Expression);
                command.Parameters.Add(
                    odbcParameter);
            }
            
            if (Debug)
                Console.WriteLine("Patient records filtered on PCEInital:");

            using (var writer = new StreamWriter("output.csv"))
            {
                var csvConfig = new CsvConfiguration(CultureInfo.CurrentCulture) { Delimiter = ";" };
                using (var csv = new CsvWriter(writer, csvConfig))
                {
                    csv.WriteHeader<CsvRow>();
                    csv.NextRecord();

                    int counter = 0;
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            counter++;
                            var patientId = reader.GetString(0);
                            var date = reader.GetDateTime(1);
                            var sctTotal = reader.GetString(2);

                            if (Debug)
                                Console.WriteLine($"Patient {patientId} {date} {sctTotal}");

                            var item = new PostCoordinatedExpression(sctTotal, null);
                            item.ResolveProcedureSites(Teeth, Surfaces);

                            List<PostCoordinatedExpression> postCoordinatedExpressionsForTooth =
                                await LookupDentalCariesSituationsForGivenTooth(item);

                            PatientEvent? nearestEvent =
                                FindNearestEventForPatientOnGivenTooth(postCoordinatedExpressionsForTooth, patientId,
                                    date,
                                    connection);
                            lastExaminationDateForPatients.TryGetValue(patientId, out DateTime lastExamDate);


                            csv.WriteRecord(new CsvRow()
                            {
                                PatientId = patientId,
                                InitDate = date,
                                InitPce = sctTotal,
                                EventDate = nearestEvent?.Date,
                                EventPce = nearestEvent?.SctTotal,
                                LastExaminationDate = lastExamDate
                            });
                            csv.NextRecord();

                            // calculate statistics
                            if (nearestEvent?.Date != null)
                            {
                                double durationInDays = nearestEvent.Date.Subtract(date).TotalDays;
                                
                                duration.Add(durationInDays);
                            } else if (lastExamDate != null)
                            {
                                double durationInDays = lastExamDate.Date.Subtract(date).TotalDays;
                                if (durationInDays >= (365 * 5)) // only count this entry if last examination was more than 5 years ago
                                {
                                    duration.Add(durationInDays);
                                }
                            }

                            if (Debug)
                                Console.WriteLine(
                                    $"{patientId};{Format(date)};{sctTotal};{Format(nearestEvent?.Date)};{nearestEvent?.SctTotal};{Format(lastExamDate)}");
                            else
                            {
                                Console.Write($"\rEvents: {counter}"); // \r resets cursor back to start of line
                            }
                        }
                        Console.WriteLine("\r                                 "); // clear line
                    }
                }
            }
        }

        double eventsCount = duration.Count;
        
        Console.WriteLine("");
        Console.WriteLine("## Statistics");
        Console.WriteLine("");
        Console.WriteLine($"{eventsCount} events included in statistics");
        double averageDuration = Math.Floor(duration.Sum() / eventsCount);
        double averageDurationInYears = Math.Round(averageDuration / 365, 2);
        Console.WriteLine($"Average duration of PCE: {averageDuration} days or {averageDurationInYears} years");

        double eventsDurationMoreThan5Year = duration.Count(d => d >= (365 * 5));
        double percentageDurationMoreThan5Year = Math.Round((eventsDurationMoreThan5Year / eventsCount) * 100,1);
        
        Console.WriteLine($"Duration more than 5 years: {percentageDurationMoreThan5Year} % ({eventsDurationMoreThan5Year} of {eventsCount} events)");
        
        double eventsDurationMoreThan10Year = duration.Count(d => d >= (365 * 10));
        double percentageDurationMoreThan10Year = Math.Round((eventsDurationMoreThan10Year / eventsCount) * 100,1);
        
        Console.WriteLine($"Duration more than 10 years: {percentageDurationMoreThan10Year} % ({eventsDurationMoreThan10Year} of {eventsCount} events)");
    }

    string? Format(DateTime? date)
    {
        return date?.ToString("yyyy-MM-dd");
    }

    Dictionary<string, DateTime> FindLastExaminationDateForPatients(DbConnection connection)
    {
        Dictionary<string, DateTime> examinationDates = new();

        var command = connection.CreateCommand();
        command.CommandText = @"SELECT
        AnoPID,
        MAX(Date) AS SisteUS,
        SCTtotal
        FROM PcJSON
        WHERE SCTtotal = '34043003'
        Group By AnoPID";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var anoPid = reader.GetString(0);
            var date = reader.GetDateTime(1);
            examinationDates.Add(anoPid, date);
        }

        return examinationDates;
    }

    PatientEvent? FindNearestEventForPatientOnGivenTooth(List<PostCoordinatedExpression> postCoordinatedExpressions,
        string patientId, DateTime initialTreatmentDate, DbConnection connection)
    {
        List<PatientEvent> events = new();

        // loop through all possible combinations and search for matches in patient history
        foreach (var item in postCoordinatedExpressions)
        {
            var command = connection.CreateCommand();
            command.CommandText =
                @"
        SELECT AnoPID,Date,SCTtotal
        FROM PcJSON
        WHERE AnoPID = ? AND SCTtotal = ?
        ORDER by Date
    ";
            
            command.Parameters.Add(new OdbcParameter("patientId", SqlDbType.VarChar) {Value = patientId});
            command.Parameters.Add(new OdbcParameter("expression", SqlDbType.VarChar) {Value = item.Expression});

            using (var reader = command.ExecuteReader())
            {
                if (Debug && reader.HasRows)
                    Console.WriteLine($"Expression: {item.Expression}");

                while (reader.Read())
                {
                    var anoPid = reader.GetString(0);
                    var date = reader.GetDateTime(1);
                    var sctTotal = reader.GetString(2);

                    if (Debug)
                        Console.WriteLine($"* Event - Patient {anoPid} {date} {sctTotal}");

                    events.Add(new PatientEvent(anoPid, date, sctTotal));
                }

                if (Debug && reader.HasRows)
                    Console.WriteLine();
            }
        }

        if (events.Any())
        {
            var sortedEvents = events.OrderBy(e => e.Date);

            // ReSharper disable PossibleMultipleEnumeration
            var closestEvent = initialTreatmentDate >= sortedEvents.Last().Date
                ? null // no treatment after initial
                : initialTreatmentDate <= sortedEvents.First().Date
                    ? sortedEvents.First() // initial treatment is before all other events
                    : sortedEvents.First(e => e.Date >= initialTreatmentDate);

            if (Debug)
                Console.WriteLine("Closest event is " + closestEvent?.Date);
            return closestEvent;
        }

        // no other event related to this expression exists. this can be a normal situation
        return null;
    }

    async Task<List<PostCoordinatedExpression>> LookupDentalCariesSituationsForGivenTooth(
        PostCoordinatedExpression item)
    {
        if (Debug)
        {
            Console.WriteLine("## Lookup Dental Caries for given expression");
            Console.WriteLine("Expression: " + item.Expression);
            Console.WriteLine("Description: " + item.Description);
            item.PrintProcedureSitesToConsole();
        }

        var url = new Url("https://xsct.norwayeast.cloudapp.azure.com/fhir/ValueSet/$expand")
            .SetQueryParam("url",
                "http://snomed.info/xsct/11000003106?fhir_vs=ecl/" + item.GenerateEclForDentalCaries());

        if (Debug)
        {
            Console.WriteLine("Looking up Dental Caries for given tooth");
            Console.WriteLine(url.ToString());
        }

        var result = await AppendBasicAuth(url).GetJsonAsync<FhirValueSetResponse>();

        var postCoordinatedExpressions = result.Expansion?.Contains
                                             ?.Select(c => new PostCoordinatedExpression(c.Code, c.Display))
                                             .ToList()
                                         ??
                                         new List<PostCoordinatedExpression>();

        if (Debug)
            Console.WriteLine($"Received {postCoordinatedExpressions.Count} expressions");

        return postCoordinatedExpressions;
    }

    async Task<List<PostCoordinatedExpression>> GetPostCoordinatedExpressions()
    {
        string selectedCardinality = AskConsoleUserAboutCardinality();
        
        var url = new Url("https://xsct.norwayeast.cloudapp.azure.com/fhir/ValueSet/$expand")
            .SetQueryParam("url",
                $"http://snomed.info/xsct/11000003106?fhir_vs=ecl/<<234789004 |Insertion of composite restoration into tooth|:{selectedCardinality}363704007 |Procedure site|=<<245644000 |Structure of single tooth surface|");

        var result = await AppendBasicAuth(url).GetJsonAsync<FhirValueSetResponse>();

        return result.Expansion?.Contains?
                   .Select(c => new PostCoordinatedExpression(c.Code, c.Display))
                   .ToList()
               ??
               new List<PostCoordinatedExpression>();
    }

    string AskConsoleUserAboutCardinality()
    {
        int[] numericOptions = { 1, 2, 3, 4, 5, 6 };
        string[] cardinalityValues = { "%5B1..1%5D", "%5B2..2%5D", "%5B3..3%5D", "%5B4..4%5D", "%5B5..5%5D", "%5B1..5%5D" };
        string[] cardinalityText = { "1 flate", "2 flater", "3 flater", "4 flater", "5 flater", "Alle" };

        for (int i = 0; i < numericOptions.Length; i++)
        {
            Console.WriteLine($"{numericOptions[i]} - {cardinalityText[i]}");
        }

        int selectedOption = GetUserNumericChoice(numericOptions);
        
        if (selectedOption < 1 || selectedOption > numericOptions.Length)
            throw new Exception("Invalid selection of cardinality: " + selectedOption);

        string selectedCardinalityValue = cardinalityValues[selectedOption - 1];
        Console.WriteLine($"You selected {selectedOption} - {selectedCardinalityValue}");
            
        return selectedCardinalityValue;
    }
    static int GetUserNumericChoice(int[] options)
    {
        // Get user input

        int selectedOption;
        while (true)
        {
            Console.Write("Enter the number of your choice: ");
            if (int.TryParse(Console.ReadLine(), out selectedOption) && options.Contains(selectedOption))
            {
                break;
            }
            Console.WriteLine("Invalid input. Please enter a valid number.");
        }

        return selectedOption;
    }
    
    
    async Task<Dictionary<string, SctConcept>> GetSurfaces()
    {
        var url = new Url("https://xsct.norwayeast.cloudapp.azure.com/fhir/ValueSet/$expand")
            .SetQueryParam("url", "http://snomed.info/xsct/11000003106?fhir_vs=ecl/<<245644000");

        var result = await AppendBasicAuth(url).GetJsonAsync<FhirValueSetResponse>();

        return result.Expansion?.Contains?
                   .Select(c => new SctConcept(c.Code, c.Display))
                   .ToDictionary(x => x.Code, x => x)
               ??
               new Dictionary<string, SctConcept>();
    }

    async Task<Dictionary<string, SctConcept>> GetTeeth()
    {
        var url = new Url("https://xsct.norwayeast.cloudapp.azure.com/fhir/ValueSet/$expand")
            .SetQueryParam("url", "http://snomed.info/xsct/11000003106?fhir_vs=ecl/<<38199008 MINUS <<410613002");

        var result = await AppendBasicAuth(url).GetJsonAsync<FhirValueSetResponse>();

        return result.Expansion?.Contains?
                   .Select(c => new SctConcept(c.Code, c.Display))
                   .ToDictionary(x => x.Code, x => x)
               ??
               new Dictionary<string, SctConcept>();
    }

    IFlurlRequest AppendBasicAuth(Url url)
    {
        string? username = Config.GetValue<string>("FhirServer:Username");
        string? password = Config.GetValue<string>("FhirServer:Password");
        return url.WithBasicAuth(username, password);
    }

}