﻿using System.Data;
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
                    
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
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



                            Console.WriteLine(
                                $"{patientId};{Format(date)};{sctTotal};{Format(nearestEvent?.Date)};{nearestEvent?.SctTotal};{Format(lastExamDate)}");
                        }
                    }
                }
            }
        }
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
        var url = new Url("https://xsct.norwayeast.cloudapp.azure.com/fhir/ValueSet/$expand")
            .SetQueryParam("url",
                "http://snomed.info/xsct/11000003106?fhir_vs=ecl/%3C%3C234785005:363701004=256452006,363704007=%3C%3C76424003,[2..2]363704007=%3C%3C245644000");

        var result = await AppendBasicAuth(url).GetJsonAsync<FhirValueSetResponse>();

        return result.Expansion?.Contains?
                   .Select(c => new PostCoordinatedExpression(c.Code, c.Display))
                   .ToList()
               ??
               new List<PostCoordinatedExpression>();
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