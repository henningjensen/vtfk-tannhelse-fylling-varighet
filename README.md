
# Konfigurasjon av autentisering mot snowstorm

Kopier appsettings.default.json til appsettings.json
Endre brukernavn og passord i filen appsettings.json


## Installasjon av ODBC-driver (ubuntu)

Install odbc driver
```
sudo apt-get install libsqliteodbc odbcinst1debian2 libodbc1 odbcinst unixodbc
```

Configure database connection
```
$ cat ~/.odbc.ini 
[Rdata]
    Description=Vtfk Tannhelse - prototype
    Driver=SQLite3
    Database=/PATH/TO/DATABASE/patients.db
    # optional lock timeout in milliseconds
    Timeout=2000
```

Enable Multiple Active Result Set

Add registry key
```
\HKLM\Software\ODBC\ODBC.INI\MyDSN
```

Add a string value:

```
Name - MARS_Connection
Value - Yes
```


## Oppsett av sqlite-database

Opprett ny database

```
sqlite3 patients.db
```

Kjør følgende kommandoer for å importere eksempeldata.csv til tabellen patients

```
.mode csv
.separator ;
.import eksempeldata.csv PcJSON
```

