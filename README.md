# 🏭 Industrial Processing System (IPS) API
### *High-Performance, Thread-Safe, Asynchronous Job Orchestration*

![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge&logo=dotnet)
![C#](https://img.shields.io/badge/C%23-12.0-239120?style=for-the-badge&logo=csharp)
![License](https://img.shields.io/badge/License-MIT-yellow?style=for-the-badge)

---

## 📖 Pregled Projekta

**Industrial Processing System (IPS)** je napredni simulacioni engine dizajniran za asinhronu obradu kompleksnih industrijskih zadataka. Razvijen kao visokoperformansni thread-safe servis, IPS demonstrira vrhunske tehnike u modernom .NET razvoju, uključujući **Producer-Consumer** model, **Prioritetne redove**, i **Event-Driven** arhitekturu.

Sistem je optimizovan za rad u real-time okruženjima gde su integritet podataka, redosled izvršavanja po prioritetima i otpornost na greške (fault tolerance) od kritične važnosti.

---

## 🏗 Arhitektonska Rešenja

### 1. Producer-Consumer Model
Sistem koristi klasičan, ali visoko asinhronizovan Producer-Consumer obrazac:
*   **Producers**: Više niti koje generišu nasumične poslove (Prime ili IO) i šalju ih u sistem putem `Submit()` metode.
*   **Central Queue**: `PriorityQueue` zaštićen monitorom (`lock`) koji automatski sortira poslove prema prioritetu (LIFO/FIFO nije bitno, bitna je vrednost prioriteta).
*   **Consumers (Workers)**: Konfigurabilan broj worker niti koje "osluškuju" red čekanja koristeći `SemaphoreSlim` za efikasnu signalizaciju bez "busy-waiting" petlje.

### 2. Event-Driven Workflow
Umesto blokirajućih poziva, sistem se oslanja na delegatski model:
*   `JobCompleted`: Emituje se čim se posao uspešno završi, vraćajući rezultat.
*   `JobFailed`: Emituje se nakon što mehanizam za retry iscrpi sve pokušaje.

### 3. Idempotentnost i Bezbednost
*   **Idempotentnost**: Svaki posao ima jedinstveni `Guid`. Sistem održava `HashSet` svih "viđenih" ID-eva, osiguravajući da se isti posao nikada ne izvrši dva puta, čak i ako se greškom pošalje ponovo.
*   **Thread-Safety**: Svaki pristup deljenim resursima (red, statistike, log fajlovi) je precizno zaključan minimalnim kritičnim sekcijama kako bi se izbegli race-conditions i deadlocks.

---

## 🛠 Tehnička Implementacija: Duboki Uvid

### ⚡ Paralelizam i Prime Engine
Kod obrade `Prime` poslova, sistem ne koristi samo jednu nit. Koristi se **Data Parallelism** putem `Parallel.For`:
*   **Dinamičko skaliranje**: Broj niti se čita iz payload-a i ograničava na opseg `[1, 8]` pomoću `Math.Clamp`.
*   **Thread Efficiency**: Koristi se `Interlocked.Add` za atomično brojanje prostih brojeva, izbegavajući skupe lock-ove unutar same kalkulacije.

### 🛡 Retry Mehanizam i Fault Tolerance
Sistem je dizajniran da preživi povremene greške:
1.  **Timeout**: Svaki posao ima striktan limit od **2000ms**.
2.  **Retry**: Ako posao baci exception ili premaši vreme, sistem ga automatski ponovo pokreće do **3 puta**.
3.  **Abort Logic**: Ako i treći pokušaj fail-uje, sistem trajno odustaje, upisuje `ABORT` status i oslobađa resurse.

### 📈 LINQ Reporting & File Management
Sistem generiše inteligenciju o svom radu na svakih 60 sekundi:
*   **Circular Buffer**: Umesto da zatrpava disk, sistem čuva samo poslednjih 10 XML izveštaja koristeći modulo aritmetiku (`index % 10`).
*   **LINQ Analysis**: Izveštaji sadrže prosečna vremena izvršavanja, procente uspešnosti i grupisanu statistiku po tipu posla.

---

## 📊 Pregled Modula

| Modul | Odgovornost |
| :--- | :--- |
| **`Core/ProcessingSystem`** | Srce sistema. Upravlja nitima, redom, logovanjem i retry logikom. |
| **`Config/ConfigLoader`** | Parsira `SystemConfig.xml` i inicijalizuje stanje sistema. |
| **`Models/Job`** | DTO koji nosi podatke o tipu, prioritetu i payload-u. |
| **`Models/JobHandle`** | Vraća `Task<int>` pozivaocu, omogućavajući neblokirajuće čekanje na rezultat. |

---

## ⚙️ Konfiguracija Sistema

Konfiguracija se vrši isključivo putem XML fajla, što omogućava promenu ponašanja sistema bez rekompilacije:

```xml
<SystemConfig>
    <!-- Broj paralelnih worker niti koje procesuiraju red -->
    <WorkerCount>5</WorkerCount>
    
    <!-- Kapacitet reda; nakon ovoga Submit() odbija nove poslove -->
    <MaxQueueSize>100</MaxQueueSize>
    
    <Jobs>
        <!-- Inicijalni poslovi koji se učitavaju na startu -->
        <Job Type="Prime" Payload="numbers:50000,threads:4" Priority="1"/>
        <Job Type="IO" Payload="delay:1500" Priority="3"/>
    </Jobs>
</SystemConfig>
```

---

## 🚀 Upotreba API-ja (Primer koda)

```csharp
// 1. Inicijalizacija
var config = ConfigLoader.Load("SystemConfig.xml");
var system = new ProcessingSystem(config.WorkerCount, config.MaxQueueSize);

// 2. Pretplata na događaje
system.JobCompleted += (job, result) => {
    Console.WriteLine($"Završeno: {job.Id} -> Rezultat: {result}");
};

// 3. Slanje posla
var myJob = new Job { 
    Id = Guid.NewGuid(), 
    Type = JobType.Prime, 
    Payload = "numbers:10000,threads:2", 
    Priority = 1 
};

var handle = system.Submit(myJob);
if (handle != null) {
    // Možete čekati asinhrono na rezultat bez blokiranja niti
    int result = await handle.Result;
}
```

---

## 🧪 Validacija i Testiranje

Sistem je izgrađen sa fokusom na **Time-independent testing**:
*   Izbegnut je `Thread.Sleep` u logici čekanja rezultata.
*   Korišćenje `TaskCompletionSource` omogućava testovima da reaguju tačno u trenutku završetka posla, čineći unit testove brzim i determinističkim.

---
*Projekat je razvijen kao deo kolokvijuma za predmet SNUS (Softver nadzorno upravljačkih sistema) 2026.*
