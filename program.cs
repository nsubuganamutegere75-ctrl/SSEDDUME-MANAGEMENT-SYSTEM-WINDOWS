// =========================================================================================
// SSEDDUME DRUG SHOP MANAGEMENT SYSTEM (DSMS) - ALL-IN-ONE ENTERPRISE ENGINE
// Compliance: Uganda National Drug Authority (NDA) Retail Pharmacy Standards
// Runtime: .NET 8.0 / C# 12 (Windows Forms + HTTP Companion + UDP Discovery)
// Architecture: Single-File Master Station with Embedded Android Mobile Terminal
// =========================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SseddumeDrugShopMaster
{
    internal static class Program
    {
        private const string ApplicationMutexGuid = "Global\\SSEDDUME_DSMS_CORE_MASTER_V7";

        [STAThread]
        static void Main()
        {
            using var systemMutex = new Mutex(true, ApplicationMutexGuid, out bool isSoleInstance);
            if (!isSoleInstance)
            {
                MessageBox.Show(
                    "An instance of Sseddume Drug Shop Master Station is already running.",
                    "Sseddume DSMS - Active Instance Detected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var stateManager = new SystemStateManager();
            stateManager.Initialize();

            var httpEngine = new MasterCompanionHttpEngine(stateManager);
            httpEngine.Start();

            var lanBeacon = new LanDiscoveryBeacon(httpEngine.AssignedPort, stateManager.StationMachineName);
            lanBeacon.Start();

            var masterDashboard = new MasterStationDashboardForm(stateManager, httpEngine);
            Application.Run(masterDashboard);

            lanBeacon.Stop();
            httpEngine.Stop();
            stateManager.PersistToDisk();
        }
    }

    #region 1. Domain Models & NDA Clinical Contracts

    public enum StaffRole { Owner = 0, Manager = 1, Dispenser = 2 }
    public enum UnitType { Pack = 0, Strip = 1, Unit = 2 }

    public sealed class MedicationBatch
    {
        public string BatchId { get; set; } = Guid.NewGuid().ToString("N");
        public string BatchNumber { get; set; } = string.Empty;
        public DateTime ExpiryDate { get; set; }
        public int UnitsRemaining { get; set; }
        public decimal UnitCostPriceUgx { get; set; }
        public decimal PackCostPriceUgx { get; set; }
        public DateTime DateReceived { get; set; } = DateTime.UtcNow;
        public bool IsQuarantined { get; set; } = false;
    }

    public sealed class MedicationItem
    {
        public string SkuId { get; set; } = Guid.NewGuid().ToString("N");
        public string Barcode { get; set; } = string.Empty;
        public string BrandName { get; set; } = string.Empty;
        public string GenericName { get; set; } = string.Empty;
        public string Category { get; set; } = "General";
        public string DosageForm { get; set; } = "Tablet";
        public string Strength { get; set; } = string.Empty;
        public int UnitsPerPack { get; set; } = 100;
        public int UnitsPerStrip { get; set; } = 10;
        public decimal RetailPackPriceUgx { get; set; }
        public decimal RetailStripPriceUgx { get; set; }
        public decimal RetailSingleUnitPriceUgx { get; set; }
        public int ReorderThresholdUnits { get; set; } = 50;
        public long SequenceVersion { get; set; } = 0;
        public List<MedicationBatch> Batches { get; set; } = new List<MedicationBatch>();

        [JsonIgnore]
        public int TotalAvailableUnits => Batches
            .Where(b => !b.IsQuarantined && b.ExpiryDate > DateTime.UtcNow)
            .Sum(b => b.UnitsRemaining);

        public List<MedicationBatch> GetFefoOrderedBatches()
        {
            return Batches
                .Where(b => !b.IsQuarantined && b.UnitsRemaining > 0 && b.ExpiryDate > DateTime.UtcNow)
                .OrderBy(b => b.ExpiryDate)
                .ToList();
        }
    }

    public sealed class StagedPrescriptionLine
    {
        public string SkuId { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public string DrugName { get; set; } = string.Empty;
        public UnitType DispenseUnit { get; set; }
        public int QuantityToDispense { get; set; }
        public decimal UnitPriceUgx { get; set; }
        public decimal LineTotalUgx { get; set; }
        public string DosageInstructions { get; set; } = string.Empty;
        public string AllocatedBatch { get; set; } = string.Empty;
    }

    public sealed class StagedCartTicket
    {
        public string TicketId { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
        public string TokenNumber { get; set; } = string.Empty;
        public string OperatorName { get; set; } = string.Empty;
        public StaffRole OperatorRole { get; set; }
        public string CustomerIdentifier { get; set; } = "Walk-In Patient";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<StagedPrescriptionLine> Items { get; set; } = new List<StagedPrescriptionLine>();
        public decimal TotalUgx => Items.Sum(i => i.LineTotalUgx);
        public bool IsSettled { get; set; } = false;
    }

    public sealed class StockAuditRecord
    {
        public string Barcode { get; set; } = string.Empty;
        public string DrugName { get; set; } = string.Empty;
        public int ExpectedUnits { get; set; }
        public int PhysicalUnits { get; set; }
        public int VarianceUnits => PhysicalUnits - ExpectedUnits;
        public string AuditorName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public sealed class SupervisorChallenge
    {
        public string ChallengeId { get; set; } = Guid.NewGuid().ToString("N")[..6].ToUpper();
        public string RequestingOperator { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public decimal TransactionAmountUgx { get; set; }
        public bool IsApproved { get; set; } = false;
        public bool IsResolved { get; set; } = false;
        public string ResolvedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class RemoteGunScanEvent
    {
        public string Barcode { get; set; } = string.Empty;
        public string Operator { get; set; } = string.Empty;
        public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
    }

    #endregion

    #region 2. System State Engine & Local Persistence

    public sealed class SystemStateManager
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private readonly ConcurrentDictionary<string, MedicationItem> _inventory = new ConcurrentDictionary<string, MedicationItem>();
        private readonly ConcurrentDictionary<string, StagedCartTicket> _stagedTickets = new ConcurrentDictionary<string, StagedCartTicket>();
        private readonly ConcurrentDictionary<string, SupervisorChallenge> _challenges = new ConcurrentDictionary<string, SupervisorChallenge>();
        private readonly ConcurrentBag<StockAuditRecord> _auditLog = new ConcurrentBag<StockAuditRecord>();
        private readonly ConcurrentQueue<RemoteGunScanEvent> _remoteGunScans = new ConcurrentQueue<RemoteGunScanEvent>();

        public string StationMachineName { get; } = Environment.MachineName;
        public long CurrentSequence { get; private set; } = 1;
        private readonly string _storageDirectory;
        private int _tokenCounter = 100;

        public event Action<RemoteGunScanEvent>? OnRemoteBarcodeReceived;

        public SystemStateManager()
        {
            _storageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SseddumeDrugShopMaster");
            Directory.CreateDirectory(_storageDirectory);
        }

        public void Initialize()
        {
            LoadFromDisk();
            SeedSampleInventoryIfEmpty();
        }

        public string AllocateCustomerToken()
        {
            int token = Interlocked.Increment(ref _tokenCounter);
            if (token > 999)
            {
                Interlocked.Exchange(ref _tokenCounter, 100);
                token = 100;
            }
            return $"T-{token}";
        }

        private void SeedSampleInventoryIfEmpty()
        {
            if (!_inventory.IsEmpty) return;

            var amox = new MedicationItem
            {
                Barcode = "501234567890",
                BrandName = "Amoxil 500mg",
                GenericName = "Amoxicillin Trihydrate",
                Category = "Antibacterial",
                DosageForm = "Capsule",
                Strength = "500mg",
                UnitsPerPack = 100,
                UnitsPerStrip = 10,
                RetailPackPriceUgx = 45000,
                RetailStripPriceUgx = 5000,
                RetailSingleUnitPriceUgx = 600,
                ReorderThresholdUnits = 50,
                SequenceVersion = ++CurrentSequence
            };
            amox.Batches.Add(new MedicationBatch
            {
                BatchNumber = "AMX24-08",
                ExpiryDate = DateTime.UtcNow.AddMonths(16),
                UnitsRemaining = 450,
                UnitCostPriceUgx = 320,
                PackCostPriceUgx = 32000
            });
            amox.Batches.Add(new MedicationBatch
            {
                BatchNumber = "AMX23-99",
                ExpiryDate = DateTime.UtcNow.AddMonths(2),
                UnitsRemaining = 50,
                UnitCostPriceUgx = 300,
                PackCostPriceUgx = 30000
            });
            _inventory[amox.Barcode] = amox;

            var coartem = new MedicationItem
            {
                Barcode = "768054321098",
                BrandName = "Coartem 20/120",
                GenericName = "Artemether / Lumefantrine",
                Category = "Antimalarial (ACT)",
                DosageForm = "Tablet",
                Strength = "20mg/120mg",
                UnitsPerPack = 24,
                UnitsPerStrip = 6,
                RetailPackPriceUgx = 24000,
                RetailStripPriceUgx = 6500,
                RetailSingleUnitPriceUgx = 1200,
                ReorderThresholdUnits = 24,
                SequenceVersion = ++CurrentSequence
            };
            coartem.Batches.Add(new MedicationBatch
            {
                BatchNumber = "CRT-8941",
                ExpiryDate = DateTime.UtcNow.AddMonths(14),
                UnitsRemaining = 144,
                UnitCostPriceUgx = 750,
                PackCostPriceUgx = 18000
            });
            _inventory[coartem.Barcode] = coartem;

            var paracetamol = new MedicationItem
            {
                Barcode = "600123498765",
                BrandName = "Panadol Extra",
                GenericName = "Paracetamol / Caffeine",
                Category = "Analgesic / Antipyretic",
                DosageForm = "Tablet",
                Strength = "500mg/65mg",
                UnitsPerPack = 100,
                UnitsPerStrip = 10,
                RetailPackPriceUgx = 25000,
                RetailStripPriceUgx = 2800,
                RetailSingleUnitPriceUgx = 300,
                ReorderThresholdUnits = 100,
                SequenceVersion = ++CurrentSequence
            };
            paracetamol.Batches.Add(new MedicationBatch
            {
                BatchNumber = "PDL-24-X",
                ExpiryDate = DateTime.UtcNow.AddMonths(26),
                UnitsRemaining = 900,
                UnitCostPriceUgx = 140,
                PackCostPriceUgx = 14000
            });
            _inventory[paracetamol.Barcode] = paracetamol;
        }

        public List<MedicationItem> GetInventoryDelta(long sinceSeq, StaffRole role)
        {
            _lock.EnterReadLock();
            try
            {
                return _inventory.Values
                    .Where(i => i.SequenceVersion > sinceSeq)
                    .Select(i =>
                    {
                        if (role == StaffRole.Owner) return i;

                        var sanitizedCopy = JsonSerializer.Deserialize<MedicationItem>(JsonSerializer.Serialize(i))!;
                        foreach (var b in sanitizedCopy.Batches)
                        {
                            b.UnitCostPriceUgx = 0;
                            b.PackCostPriceUgx = 0;
                        }
                        return sanitizedCopy;
                    })
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public bool StageTicket(StagedCartTicket ticket)
        {
            if (string.IsNullOrWhiteSpace(ticket.TokenNumber))
                ticket.TokenNumber = AllocateCustomerToken();

            _stagedTickets[ticket.TicketId] = ticket;
            return true;
        }

        public List<StagedCartTicket> GetPendingTickets()
        {
            return _stagedTickets.Values
                .Where(t => !t.IsSettled)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();
        }

        public (bool Success, string Message) FinalizeDispense(string ticketId, string cashierName)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_stagedTickets.TryGetValue(ticketId, out var ticket) || ticket.IsSettled)
                    return (false, "Ticket is invalid or already finalized.");

                foreach (var line in ticket.Items)
                {
                    if (!_inventory.TryGetValue(line.Barcode, out var item))
                        return (false, $"Product with barcode {line.Barcode} was not found.");

                    int unitsNeeded = line.DispenseUnit switch
                    {
                        UnitType.Pack => line.QuantityToDispense * item.UnitsPerPack,
                        UnitType.Strip => line.QuantityToDispense * item.UnitsPerStrip,
                        _ => line.QuantityToDispense
                    };

                    if (item.TotalAvailableUnits < unitsNeeded)
                        return (false, $"Insufficient stock for {item.BrandName}. Requested: {unitsNeeded}, Available: {item.TotalAvailableUnits}");
                }

                foreach (var line in ticket.Items)
                {
                    var item = _inventory[line.Barcode];
                    int unitsRemainingToDeduct = line.DispenseUnit switch
                    {
                        UnitType.Pack => line.QuantityToDispense * item.UnitsPerPack,
                        UnitType.Strip => line.QuantityToDispense * item.UnitsPerStrip,
                        _ => line.QuantityToDispense
                    };

                    var fefoBatches = item.GetFefoOrderedBatches();
                    foreach (var batch in fefoBatches)
                    {
                        if (unitsRemainingToDeduct <= 0) break;

                        if (batch.UnitsRemaining >= unitsRemainingToDeduct)
                        {
                            batch.UnitsRemaining -= unitsRemainingToDeduct;
                            line.AllocatedBatch = batch.BatchNumber;
                            unitsRemainingToDeduct = 0;
                        }
                        else
                        {
                            unitsRemainingToDeduct -= batch.UnitsRemaining;
                            batch.UnitsRemaining = 0;
                            line.AllocatedBatch = batch.BatchNumber;
                        }
                    }
                    item.SequenceVersion = ++CurrentSequence;
                }

                ticket.IsSettled = true;
                return (true, $"Dispense finalized for Token {ticket.TokenNumber}. Total: {ticket.TotalUgx:N0} UGX");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public StockAuditRecord RecordBlindAudit(string barcode, int physicalCount, string auditorName)
        {
            _lock.EnterWriteLock();
            try
            {
                _inventory.TryGetValue(barcode, out var item);
                int expected = item?.TotalAvailableUnits ?? 0;
                string drugName = item != null ? item.BrandName : "Unknown SKU";

                var record = new StockAuditRecord
                {
                    Barcode = barcode,
                    DrugName = drugName,
                    ExpectedUnits = expected,
                    PhysicalUnits = physicalCount,
                    AuditorName = auditorName
                };

                _auditLog.Add(record);
                return record;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public List<StockAuditRecord> GetAuditLog() => _auditLog.OrderByDescending(a => a.Timestamp).Take(100).ToList();

        public SupervisorChallenge CreateSupervisorChallenge(string operatorName, string reason, decimal amount)
        {
            var chal = new SupervisorChallenge
            {
                RequestingOperator = operatorName,
                Reason = reason,
                TransactionAmountUgx = amount
            };
            _challenges[chal.ChallengeId] = chal;
            return chal;
        }

        public bool ResolveSupervisorChallenge(string challengeId, bool approve, string managerName)
        {
            if (_challenges.TryGetValue(challengeId, out var chal))
            {
                chal.IsApproved = approve;
                chal.IsResolved = true;
                chal.ResolvedBy = managerName;
                return true;
            }
            return false;
        }

        public List<SupervisorChallenge> GetPendingChallenges()
        {
            return _challenges.Values.Where(c => !c.IsResolved).OrderBy(c => c.CreatedAt).ToList();
        }

        public void PostRemoteGunScan(string barcode, string op)
        {
            var ev = new RemoteGunScanEvent { Barcode = barcode, Operator = op };
            _remoteGunScans.Enqueue(ev);
            while (_remoteGunScans.Count > 50) _remoteGunScans.TryDequeue(out _);
            OnRemoteBarcodeReceived?.Invoke(ev);
        }

        public void PersistToDisk()
        {
            _lock.EnterReadLock();
            try
            {
                string path = Path.Combine(_storageDirectory, "sseddume_inventory.json");
                string json = JsonSerializer.Serialize(_inventory.Values, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        private void LoadFromDisk()
        {
            string path = Path.Combine(_storageDirectory, "sseddume_inventory.json");
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var items = JsonSerializer.Deserialize<List<MedicationItem>>(json);
                    if (items != null)
                    {
                        foreach (var it in items)
                        {
                            _inventory[it.Barcode] = it;
                            if (it.SequenceVersion > CurrentSequence)
                                CurrentSequence = it.SequenceVersion;
                        }
                    }
                }
                catch
                {
                    // Fallback gracefully on new environments
                }
            }
        }
    }

    #endregion

    #region 3. Win32 ESC/POS Raw Thermal Spooler & Aux Labels

    public static class ThermalPrinterSpooler
    {
        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] ref DOCINFOA di);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        [StructLayout(LayoutKind.Sequential)]
        private struct DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
        }

        public static bool SendRawBytes(string printerName, byte[] bytes)
        {
            if (string.IsNullOrWhiteSpace(printerName)) return false;

            var di = new DOCINFOA { pDocName = "Sseddume DSMS Print Job", pDataType = "RAW" };
            if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero)) return false;

            try
            {
                if (!StartDocPrinter(hPrinter, 1, ref di)) return false;
                if (!StartPagePrinter(hPrinter)) return false;

                IntPtr pUnmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, pUnmanagedBytes, bytes.Length);
                bool success = WritePrinter(hPrinter, pUnmanagedBytes, bytes.Length, out int _);
                Marshal.FreeCoTaskMem(pUnmanagedBytes);

                EndPagePrinter(hPrinter);
                EndDocPrinter(hPrinter);
                return success;
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }

        public static byte[] GenerateSalesReceipt(StagedCartTicket ticket, string cashier)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.ASCII);

            w.Write(new byte[] { 0x1B, 0x40 }); // Initialize ESC/POS
            w.Write(new byte[] { 0x1B, 0x61, 0x01 }); // Center
            w.Write(new byte[] { 0x1B, 0x45, 0x01 }); // Bold ON
            w.Write(Encoding.ASCII.GetBytes("SSEDDUME DRUG SHOP\n"));
            w.Write(new byte[] { 0x1B, 0x45, 0x00 }); // Bold OFF
            w.Write(Encoding.ASCII.GetBytes("NDA REG: NDA/DS/UG/8841\n"));
            w.Write(Encoding.ASCII.GetBytes("KASUBI MAIN ROAD, KAMPALA\n"));
            w.Write(Encoding.ASCII.GetBytes("TEL: +256 700 000 000 / +256 770 000 000\n"));
            w.Write(Encoding.ASCII.GetBytes("------------------------------------------\n"));

            w.Write(new byte[] { 0x1B, 0x61, 0x00 }); // Left
            w.Write(Encoding.ASCII.GetBytes($"TOKEN: {ticket.TokenNumber}     REF: #{ticket.TicketId}\n"));
            w.Write(Encoding.ASCII.GetBytes($"DATE:  {DateTime.Now:dd-MMM-yyyy HH:mm:ss}\n"));
            w.Write(Encoding.ASCII.GetBytes($"DISPENSER: {ticket.OperatorName}   CASHIER: {cashier}\n"));
            w.Write(Encoding.ASCII.GetBytes($"PATIENT:   {ticket.CustomerIdentifier}\n"));
            w.Write(Encoding.ASCII.GetBytes("------------------------------------------\n"));

            foreach (var item in ticket.Items)
            {
                w.Write(Encoding.ASCII.GetBytes($"{item.DrugName}\n"));
                string qtyCol = $"  {item.QuantityToDispense} {item.DispenseUnit.ToString().ToUpper()} @ {item.UnitPriceUgx:N0}";
                string totCol = $"{item.LineTotalUgx:N0} UGX";
                string pad = new string(' ', Math.Max(1, 42 - (qtyCol.Length + totCol.Length)));
                w.Write(Encoding.ASCII.GetBytes(qtyCol + pad + totCol + "\n"));

                if (!string.IsNullOrWhiteSpace(item.DosageInstructions))
                {
                    w.Write(Encoding.ASCII.GetBytes($"  Sig: {item.DosageInstructions}\n"));
                }
            }

            w.Write(Encoding.ASCII.GetBytes("==========================================\n"));
            w.Write(new byte[] { 0x1B, 0x45, 0x01 });
            string grandLbl = "GRAND TOTAL:";
            string grandVal = $"{ticket.TotalUgx:N0} UGX";
            string totalPad = new string(' ', Math.Max(1, 42 - (grandLbl.Length + grandVal.Length)));
            w.Write(Encoding.ASCII.GetBytes(grandLbl + totalPad + grandVal + "\n"));
            w.Write(new byte[] { 0x1B, 0x45, 0x00 });
            w.Write(Encoding.ASCII.GetBytes("------------------------------------------\n"));

            w.Write(new byte[] { 0x1B, 0x61, 0x01 });
            w.Write(Encoding.ASCII.GetBytes("Medicines once sold cannot be returned.\n"));
            w.Write(Encoding.ASCII.GetBytes("Keep all medicines out of reach of children.\n"));
            w.Write(Encoding.ASCII.GetBytes("Quick Recovery!\n\n\n\n"));
            w.Write(new byte[] { 0x1D, 0x56, 0x41, 0x10 }); // Cut Paper

            return ms.ToArray();
        }

        public static byte[] GenerateDosageAuxiliaryLabel(StagedPrescriptionLine line, string patientName)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.ASCII);

            w.Write(new byte[] { 0x1B, 0x40 });
            w.Write(new byte[] { 0x1B, 0x61, 0x01 });
            w.Write(new byte[] { 0x1B, 0x45, 0x01 });
            w.Write(Encoding.ASCII.GetBytes("SSEDDUME DRUG SHOP - DISPENSARY\n"));
            w.Write(new byte[] { 0x1B, 0x45, 0x00 });
            w.Write(Encoding.ASCII.GetBytes($"Patient: {patientName}   Date: {DateTime.Now:dd/MM/yy}\n"));
            w.Write(Encoding.ASCII.GetBytes("------------------------------------------\n"));
            w.Write(new byte[] { 0x1B, 0x61, 0x00 });
            w.Write(new byte[] { 0x1B, 0x45, 0x01 });
            w.Write(Encoding.ASCII.GetBytes($"DRUG: {line.DrugName}\n"));
            w.Write(new byte[] { 0x1B, 0x45, 0x00 });
            w.Write(Encoding.ASCII.GetBytes($"QTY:  {line.QuantityToDispense} ({line.DispenseUnit})   LOT: {line.AllocatedBatch}\n"));
            w.Write(Encoding.ASCII.GetBytes("DIRECTIONS / FREQUENCY:\n"));
            w.Write(new byte[] { 0x1B, 0x45, 0x01 });
            w.Write(Encoding.ASCII.GetBytes($" >> {line.DosageInstructions} <<\n"));
            w.Write(new byte[] { 0x1B, 0x45, 0x00 });
            w.Write(Encoding.ASCII.GetBytes("------------------------------------------\n"));
            w.Write(new byte[] { 0x1B, 0x61, 0x01 });
            w.Write(Encoding.ASCII.GetBytes("Complete the prescribed course.\n\n\n"));
            w.Write(new byte[] { 0x1D, 0x56, 0x41, 0x10 });

            return ms.ToArray();
        }
    }

    #endregion

    #region 4. Asynchronous HTTP Server & Embedded Android Web Terminal

    public sealed class MasterCompanionHttpEngine
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly SystemStateManager _state;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        public int AssignedPort { get; private set; } = 8080;

        public MasterCompanionHttpEngine(SystemStateManager state)
        {
            _state = state;
        }

        public void Start()
        {
            int[] candidatePorts = { 8080, 8081, 8082, 8085, 9090 };
            foreach (var port in candidatePorts)
            {
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://*:{port}/");
                    _listener.Start();
                    AssignedPort = port;
                    break;
                }
                catch
                {
                    try
                    {
                        _listener.Prefixes.Clear();
                        _listener.Prefixes.Add($"http://localhost:{port}/");
                        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                        _listener.Start();
                        AssignedPort = port;
                        break;
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => RouteRequestAsync(ctx), ct);
                }
                catch
                {
                    if (ct.IsCancellationRequested) break;
                }
            }
        }

        private async Task RouteRequestAsync(HttpListenerContext ctx)
        {
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Staff-Role, X-Operator-Name");

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
                return;
            }

            string path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            string method = ctx.Request.HttpMethod;

            try
            {
                // Serve Embedded Android Companion Terminal directly from memory
                if (method == "GET" && (path == "" || path.Equals("/m", StringComparison.OrdinalIgnoreCase) || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)))
                {
                    byte[] htmlBytes = Encoding.UTF8.GetBytes(EmbeddedAndroidAppHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = htmlBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(htmlBytes);
                    ctx.Response.Close();
                    return;
                }

                // Inventory Sequence Delta Sync
                if (method == "GET" && path.Equals("/api/sync/delta", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(ctx.Request.QueryString["since"], out long since);
                    StaffRole role = StaffRole.Dispenser;
                    if (Enum.TryParse<StaffRole>(ctx.Request.Headers["X-Staff-Role"], true, out var r))
                        role = r;

                    var delta = _state.GetInventoryDelta(since, role);
                    await WriteJsonAsync(ctx.Response, 200, new
                    {
                        serverTime = DateTime.UtcNow,
                        currentSequence = _state.CurrentSequence,
                        items = delta
                    });
                    return;
                }

                // Remote Gun Scan Endpoint
                if (method == "POST" && path.Equals("/api/pos/scan", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    string body = await reader.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string barcode = doc.RootElement.GetProperty("barcode").GetString() ?? "";
                    string op = doc.RootElement.GetProperty("operator").GetString() ?? "Mobile Gun";

                    _state.PostRemoteGunScan(barcode, op);
                    await WriteJsonAsync(ctx.Response, 200, new { success = true, barcode });
                    return;
                }

                // Stage Prescription Ticket
                if (method == "POST" && path.Equals("/api/tickets/stage", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    string body = await reader.ReadToEndAsync();
                    var ticket = JsonSerializer.Deserialize<StagedCartTicket>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (ticket != null)
                    {
                        _state.StageTicket(ticket);
                        await WriteJsonAsync(ctx.Response, 200, new { success = true, ticketId = ticket.TicketId, tokenNumber = ticket.TokenNumber });
                    }
                    else
                    {
                        await WriteJsonAsync(ctx.Response, 400, new { error = "Invalid ticket format." });
                    }
                    return;
                }

                // Active Tickets Query
                if (method == "GET" && path.Equals("/api/tickets/active", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(ctx.Response, 200, _state.GetPendingTickets());
                    return;
                }

                // Blind Stock Audit Scan
                if (method == "POST" && path.Equals("/api/audit/log", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    string body = await reader.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string barcode = doc.RootElement.GetProperty("barcode").GetString() ?? "";
                    int count = doc.RootElement.GetProperty("count").GetInt32();
                    string auditor = doc.RootElement.GetProperty("auditor").GetString() ?? "Auditor";

                    var rec = _state.RecordBlindAudit(barcode, count, auditor);
                    await WriteJsonAsync(ctx.Response, 200, rec);
                    return;
                }

                // Supervisor Challenge Creation
                if (method == "POST" && path.Equals("/api/supervisor/challenge", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    string body = await reader.ReadToEndAsync();
                    var req = JsonSerializer.Deserialize<SupervisorChallenge>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (req != null)
                    {
                        var chal = _state.CreateSupervisorChallenge(req.RequestingOperator, req.Reason, req.TransactionAmountUgx);
                        await WriteJsonAsync(ctx.Response, 200, chal);
                    }
                    return;
                }

                // Supervisor Pending List
                if (method == "GET" && path.Equals("/api/supervisor/pending", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(ctx.Response, 200, _state.GetPendingChallenges());
                    return;
                }

                // Supervisor Challenge Resolution
                if (method == "POST" && path.Equals("/api/supervisor/resolve", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    string body = await reader.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string chalId = doc.RootElement.GetProperty("challengeId").GetString() ?? "";
                    bool approved = doc.RootElement.GetProperty("approve").GetBoolean();
                    string mgr = doc.RootElement.GetProperty("managerName").GetString() ?? "In-Charge";

                    bool success = _state.ResolveSupervisorChallenge(chalId, approved, mgr);
                    await WriteJsonAsync(ctx.Response, 200, new { success });
                    return;
                }

                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await WriteJsonAsync(ctx.Response, 500, new { error = ex.Message });
            }
        }

        private static async Task WriteJsonAsync(HttpListenerResponse res, int statusCode, object obj)
        {
            res.StatusCode = statusCode;
            res.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }

        #region Embedded Android Companion Client HTML5 / JS / CSS Source
        private const string EmbeddedAndroidAppHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no">
    <title>Sseddume DSMS Mobile Terminal</title>
    <style>
        :root {
            --bg-base: #0f172a; --bg-surface: #1e293b; --bg-elevated: #334155;
            --border: #475569; --primary: #0284c7; --accent: #38bdf8;
            --text-main: #f8fafc; --text-muted: #94a3b8; --success: #10b981;
            --danger: #ef4444; --warning: #f59e0b;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; user-select: none; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
        body { background: var(--bg-base); color: var(--text-main); height: 100vh; display: flex; flex-direction: column; overflow: hidden; }
        header { background: var(--bg-surface); padding: 10px 14px; border-bottom: 1px solid var(--border); display: flex; justify-content: space-between; align-items: center; }
        .brand { font-size: 0.95rem; font-weight: 700; color: var(--accent); }
        .station-indicator { display: flex; align-items: center; gap: 6px; font-size: 0.72rem; color: var(--text-muted); }
        .status-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--danger); }
        .status-dot.online { background: var(--success); box-shadow: 0 0 6px var(--success); }
        nav { background: var(--bg-surface); display: flex; border-bottom: 1px solid var(--border); }
        .nav-btn { flex: 1; padding: 10px 0; font-size: 0.72rem; font-weight: 600; color: var(--text-muted); background: none; border: none; border-bottom: 3px solid transparent; text-transform: uppercase; }
        .nav-btn.active { color: var(--accent); border-bottom-color: var(--accent); background: rgba(56, 189, 248, 0.05); }
        main { flex: 1; overflow-y: auto; padding: 10px; display: flex; flex-direction: column; gap: 8px; }
        .view-panel { display: none; height: 100%; flex-direction: column; gap: 8px; }
        .view-panel.active { display: flex; }
        .search-box { display: flex; gap: 6px; }
        input, select { width: 100%; padding: 10px; background: var(--bg-surface); border: 1px solid var(--border); border-radius: 6px; color: var(--text-main); font-size: 0.88rem; outline: none; }
        input:focus { border-color: var(--accent); }
        .btn-icon { width: 44px; background: var(--bg-elevated); border: 1px solid var(--border); border-radius: 6px; color: #fff; font-size: 1.1rem; }
        .card-list { flex: 1; overflow-y: auto; display: flex; flex-direction: column; gap: 8px; }
        .item-card { background: var(--bg-surface); border: 1px solid var(--border); border-radius: 8px; padding: 10px; display: flex; flex-direction: column; gap: 6px; }
        .item-header { display: flex; justify-content: space-between; align-items: baseline; }
        .drug-title { font-size: 0.92rem; font-weight: 600; }
        .drug-cat { font-size: 0.7rem; color: var(--accent); text-transform: uppercase; }
        .unit-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 6px; }
        .unit-chip { padding: 6px; background: var(--bg-elevated); border: 1px solid var(--border); border-radius: 4px; text-align: center; font-size: 0.72rem; cursor: pointer; }
        .unit-chip.selected { background: var(--primary); border-color: var(--accent); font-weight: 700; color: #fff; }
        .sig-grid { display: flex; flex-wrap: wrap; gap: 4px; }
        .sig-chip { font-size: 0.65rem; background: var(--bg-elevated); border: 1px solid var(--border); padding: 4px 6px; border-radius: 4px; color: var(--text-muted); cursor: pointer; }
        .btn-stage { background: var(--primary); color: #fff; border: none; padding: 10px 16px; border-radius: 6px; font-weight: 700; font-size: 0.88rem; }
        .bottom-bar { background: var(--bg-surface); padding: 10px 14px; border-top: 1px solid var(--border); display: flex; justify-content: space-between; align-items: center; }
        #scanner-modal { display: none; position: fixed; inset: 0; background: #000; z-index: 1000; flex-direction: column; }
        #scanner-video { flex: 1; object-fit: cover; }
        .scanner-hud { position: absolute; top: 16px; left: 16px; right: 16px; display: flex; justify-content: space-between; }
    </style>
</head>
<body>
    <header>
        <div>
            <div class="brand">SSEDDUME DSMS</div>
            <div class="station-indicator">
                <div class="status-dot" id="station-dot"></div>
                <span id="station-text">Connecting to Master...</span>
            </div>
        </div>
        <select id="operator-role" style="width: auto; padding: 4px 8px; font-size: 0.72rem;">
            <option value="Dispenser">Dispenser</option>
            <option value="Manager">Manager</option>
            <option value="Owner">Owner</option>
        </select>
    </header>

    <nav>
        <button class="nav-btn active" onclick="switchView('tab-dispense', this)">Prescription POS</button>
        <button class="nav-btn" onclick="switchView('tab-gun', this)">Remote Gun</button>
        <button class="nav-btn" onclick="switchView('tab-audit', this)">Blind Audit</button>
        <button class="nav-btn" onclick="switchView('tab-supervisor', this)">Supervisor</button>
    </nav>

    <main>
        <!-- 1. POS Prescriptions -->
        <div id="tab-dispense" class="view-panel active">
            <div class="search-box">
                <input type="text" id="drug-search" placeholder="Scan Barcode or Search Drug..." onkeyup="filterCatalog(this.value)">
                <button class="btn-icon" onclick="openScanner('dispense')">📷</button>
            </div>
            <div class="card-list" id="catalog-matches"></div>
            <div style="font-size: 0.72rem; color: var(--text-muted); text-transform: uppercase;">Active Prescription Basket</div>
            <div class="card-list" id="active-cart" style="max-height: 40%;"></div>
        </div>

        <!-- 2. Remote Gun -->
        <div id="tab-gun" class="view-panel">
            <div style="text-align: center; padding: 1.5rem 1rem;">
                <h3 style="color: var(--accent); margin-bottom: 0.5rem;">Wireless Barcode Gun Mode</h3>
                <p style="font-size: 0.8rem; color: var(--text-muted); margin-bottom: 1.5rem;">
                    Scans captured here transmit straight to the Windows Master register input box in real time.
                </p>
                <button class="btn-stage" style="padding: 1rem 2rem; font-size: 1rem;" onclick="openScanner('gun')">
                    📷 Launch Camera Gun
                </button>
                <div id="gun-last-scan" style="margin-top: 1.5rem; font-size: 0.85rem; color: var(--success); font-weight: bold;"></div>
            </div>
        </div>

        <!-- 3. Blind Audit -->
        <div id="tab-audit" class="view-panel">
            <div class="search-box">
                <input type="text" id="audit-sku" placeholder="Scan Shelf SKU for Audit...">
                <button class="btn-icon" onclick="openScanner('audit')">📷</button>
            </div>
            <div style="display: flex; gap: 8px;">
                <input type="number" id="audit-qty" placeholder="Physical Units Counted" style="flex: 2;">
                <button class="btn-stage" style="flex: 1;" onclick="commitAuditLog()">Log Count</button>
            </div>
            <div class="card-list" id="audit-history"></div>
        </div>

        <!-- 4. Supervisor Challenges -->
        <div id="tab-supervisor" class="view-panel">
            <div style="font-size: 0.75rem; color: var(--text-muted); margin-bottom: 4px;">PENDING OVERRIDE REQUESTS</div>
            <div class="card-list" id="challenges-list"></div>
        </div>
    </main>

    <div class="bottom-bar" id="cart-footer">
        <div>
            <div style="font-size: 0.68rem; color: var(--text-muted);">PRESCRIPTION TOTAL</div>
            <div style="font-size: 1.15rem; font-weight: 700; color: var(--accent);" id="cart-total">0 UGX</div>
        </div>
        <button class="btn-stage" onclick="stagePrescriptionTicket()">Stage to Checkout &gt;&gt;</button>
    </div>

    <!-- Scanner Overlay -->
    <div id="scanner-modal">
        <div class="scanner-hud">
            <span style="color:#fff; background:rgba(0,0,0,0.6); padding:4px 8px; border-radius:4px; font-size:0.8rem;">Target Barcode</span>
            <button style="background:var(--danger); color:#fff; border:none; padding:6px 12px; border-radius:4px;" onclick="closeScanner()">Close</button>
        </div>
        <video id="scanner-video" playsinline></video>
    </div>

    <script>
        const baseUrl = window.location.origin;
        let lastSeq = 0, catalog = [], cart = [], selectedUnits = {}, selectedSigs = {};
        let activeContext = 'dispense', videoStream = null;

        const audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        function beep(ok = true) {
            try {
                const o = audioCtx.createOscillator(), g = audioCtx.createGain();
                o.connect(g); g.connect(audioCtx.destination);
                o.frequency.setValueAtTime(ok ? 1850 : 380, audioCtx.currentTime);
                g.gain.setValueAtTime(0.15, audioCtx.currentTime);
                g.gain.exponentialRampToValueAtTime(0.01, audioCtx.currentTime + 0.15);
                o.start(); o.stop(audioCtx.currentTime + 0.15);
                if (navigator.vibrate) navigator.vibrate(ok ? 40 : 150);
            } catch(e) {}
        }

        function switchView(tabId, btn) {
            document.querySelectorAll('.nav-btn').forEach(b => b.classList.remove('active'));
            document.querySelectorAll('.view-panel').forEach(p => p.classList.remove('active'));
            btn.classList.add('active');
            document.getElementById(tabId).classList.add('active');
            document.getElementById('cart-footer').style.display = (tabId === 'tab-dispense') ? 'flex' : 'none';
            if (tabId === 'tab-supervisor') loadSupervisorChallenges();
        }

        async function syncDelta() {
            try {
                const role = document.getElementById('operator-role').value;
                const r = await fetch(`${baseUrl}/api/sync/delta?since=${lastSeq}`, { headers: { 'X-Staff-Role': role } });
                if (r.ok) {
                    const data = await r.json();
                    if (data.items && data.items.length > 0) {
                        data.items.forEach(newItem => {
                            const idx = catalog.findIndex(c => c.Barcode === newItem.Barcode);
                            if (idx >= 0) catalog[idx] = newItem; else catalog.push(newItem);
                        });
                        lastSeq = data.currentSequence;
                    }
                    document.getElementById('station-dot').className = "status-dot online";
                    document.getElementById('station-text').textContent = "Workstation Online";
                }
            } catch(e) {
                document.getElementById('station-dot').className = "status-dot";
                document.getElementById('station-text').textContent = "Offline (Local Queue)";
            }
        }

        function filterCatalog(q) {
            const list = document.getElementById('catalog-matches');
            if (!q || q.length < 2) { list.innerHTML = ""; return; }
            const matches = catalog.filter(c => c.BrandName.toLowerCase().includes(q.toLowerCase()) || c.GenericName.toLowerCase().includes(q.toLowerCase()) || c.Barcode === q);
            list.innerHTML = matches.map(m => `
                <div class="item-card">
                    <div class="item-header">
                        <div>
                            <div class="drug-title">${m.BrandName}</div>
                            <div class="drug-cat">${m.GenericName} | ${m.Category}</div>
                        </div>
                        <span style="font-size:0.75rem; color:var(--success); font-weight:700;">${m.TotalAvailableUnits} units</span>
                    </div>
                    <div class="unit-grid">
                        <div class="unit-chip selected" onclick="chooseUnit(this, '${m.Barcode}', 0)">Pack (${m.RetailPackPriceUgx.toLocaleString()} UGX)</div>
                        <div class="unit-chip" onclick="chooseUnit(this, '${m.Barcode}', 1)">Strip (${m.RetailStripPriceUgx.toLocaleString()} UGX)</div>
                        <div class="unit-chip" onclick="chooseUnit(this, '${m.Barcode}', 2)">Unit (${m.RetailSingleUnitPriceUgx.toLocaleString()} UGX)</div>
                    </div>
                    <div class="sig-grid">
                        <span class="sig-chip" onclick="chooseSig('${m.Barcode}', '1 TDS x 5/7 (After Food)')">1 TDS x 5/7</span>
                        <span class="sig-chip" onclick="chooseSig('${m.Barcode}', '1 BD x 3/7')">1 BD x 3/7</span>
                        <span class="sig-chip" onclick="chooseSig('${m.Barcode}', '2 STAT then 1 OD')">STAT -> OD</span>
                        <span class="sig-chip" onclick="chooseSig('${m.Barcode}', '1 PRN (Pain)')">1 PRN</span>
                    </div>
                    <button class="btn-stage" style="margin-top:4px; padding:6px;" onclick="addToBasket('${m.Barcode}')">+ Add To Prescription</button>
                </div>
            `).join('');
        }

        function chooseUnit(el, barcode, u) {
            el.parentElement.querySelectorAll('.unit-chip').forEach(c => c.classList.remove('selected'));
            el.classList.add('selected');
            selectedUnits[barcode] = u;
        }

        function chooseSig(barcode, sig) { selectedSigs[barcode] = sig; beep(true); }

        function addToBasket(barcode) {
            const item = catalog.find(c => c.Barcode === barcode);
            if (!item) return;
            const u = selectedUnits[barcode] ?? 0;
            const price = u === 0 ? item.RetailPackPriceUgx : (u === 1 ? item.RetailStripPriceUgx : item.RetailSingleUnitPriceUgx);
            const sig = selectedSigs[barcode] || "As Directed";

            cart.push({
                SkuId: item.SkuId,
                Barcode: item.Barcode,
                DrugName: item.BrandName,
                DispenseUnit: u,
                QuantityToDispense: 1,
                UnitPriceUgx: price,
                LineTotalUgx: price,
                DosageInstructions: sig,
                AllocatedBatch: ""
            });
            beep(true);
            renderBasket();
        }

        function renderBasket() {
            const cList = document.getElementById('active-cart');
            let sum = 0;
            cList.innerHTML = cart.map((line, idx) => {
                sum += line.LineTotalUgx;
                const unitName = line.DispenseUnit === 0 ? 'Pack' : (line.DispenseUnit === 1 ? 'Strip' : 'Unit');
                return `
                    <div class="item-card">
                        <div class="item-header">
                            <span class="drug-title">${line.DrugName} (${unitName})</span>
                            <span style="color:var(--accent); font-weight:700;">${line.LineTotalUgx.toLocaleString()} UGX</span>
                        </div>
                        <div style="font-size:0.7rem; color:var(--text-muted);">Sig: ${line.DosageInstructions}</div>
                        <div style="display:flex; justify-content:space-between; align-items:center; margin-top:4px;">
                            <div>
                                <button style="width:26px; height:26px; background:var(--bg-elevated); border:1px solid var(--border); color:#fff;" onclick="adjustQty(${idx}, -1)">-</button>
                                <span style="margin:0 8px; font-weight:700;">${line.QuantityToDispense}</span>
                                <button style="width:26px; height:26px; background:var(--bg-elevated); border:1px solid var(--border); color:#fff;" onclick="adjustQty(${idx}, 1)">+</button>
                            </div>
                            <button style="background:none; border:none; color:var(--danger); font-size:0.75rem;" onclick="removeBasketLine(${idx})">Remove</button>
                        </div>
                    </div>
                `;
            }).join('');
            document.getElementById('cart-total').textContent = sum.toLocaleString() + " UGX";
        }

        function adjustQty(idx, d) {
            cart[idx].QuantityToDispense += d;
            if (cart[idx].QuantityToDispense <= 0) cart.splice(idx, 1);
            else cart[idx].LineTotalUgx = cart[idx].QuantityToDispense * cart[idx].UnitPriceUgx;
            renderBasket();
        }

        function removeBasketLine(idx) { cart.splice(idx, 1); renderBasket(); }

        async function stagePrescriptionTicket() {
            if (cart.length === 0) return alert("Prescription cart is empty.");
            const payload = {
                TicketId: "",
                TokenNumber: "",
                OperatorName: "Floor Dispenser",
                OperatorRole: 2,
                CustomerIdentifier: "Walk-In Patient",
                Items: cart
            };

            const r = await fetch(`${baseUrl}/api/tickets/stage`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            if (r.ok) {
                const res = await r.json();
                beep(true);
                alert(`Prescription Staged!\nToken Number: ${res.tokenNumber}\nCustomer may proceed to checkout register.`);
                cart = [];
                renderBasket();
            } else {
                alert("Station error staging ticket.");
            }
        }

        async function commitAuditLog() {
            const barcode = document.getElementById('audit-sku').value.trim();
            const count = parseInt(document.getElementById('audit-qty').value, 10);
            if (!barcode || isNaN(count)) return alert("Barcode and Count are required.");

            const r = await fetch(`${baseUrl}/api/audit/log`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ barcode, count, auditor: "Floor Staff" })
            });
            if (r.ok) {
                const data = await r.json();
                beep(true);
                document.getElementById('audit-sku').value = "";
                document.getElementById('audit-qty').value = "";
                const hist = document.getElementById('audit-history');
                hist.innerHTML = `
                    <div class="item-card">
                        <div class="item-header">
                            <span>${data.DrugName}</span>
                            <span style="font-weight:700; color:var(--accent);">${data.PhysicalUnits} Units</span>
                        </div>
                        <div style="font-size:0.7rem; color:var(--text-muted);">${new Date(data.Timestamp).toLocaleTimeString()}</div>
                    </div>
                ` + hist.innerHTML;
            }
        }

        async function loadSupervisorChallenges() {
            const r = await fetch(`${baseUrl}/api/supervisor/pending`);
            if (r.ok) {
                const list = await r.json();
                const container = document.getElementById('challenges-list');
                if (list.length === 0) {
                    container.innerHTML = '<div style="text-align:center; color:var(--text-muted); margin-top:2rem;">No pending supervisor challenges.</div>';
                    return;
                }
                container.innerHTML = list.map(c => `
                    <div class="item-card">
                        <div class="item-header">
                            <span class="drug-title">Ref #${c.ChallengeId}</span>
                            <span style="color:var(--warning); font-weight:700;">${c.TransactionAmountUgx.toLocaleString()} UGX</span>
                        </div>
                        <div style="font-size:0.75rem;">Requester: ${c.RequestingOperator}</div>
                        <div style="font-size:0.75rem; color:var(--text-muted);">Reason: ${c.Reason}</div>
                        <div style="display:flex; gap:8px; margin-top:6px;">
                            <button class="btn-stage" style="flex:1; background:var(--success);" onclick="resolveChallenge('${c.ChallengeId}', true)">Approve</button>
                            <button class="btn-stage" style="flex:1; background:var(--danger);" onclick="resolveChallenge('${c.ChallengeId}', false)">Reject</button>
                        </div>
                    </div>
                `).join('');
            }
        }

        async function resolveChallenge(challengeId, approve) {
            const r = await fetch(`${baseUrl}/api/supervisor/resolve`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ challengeId, approve, managerName: "Supervisor Handset" })
            });
            if (r.ok) {
                beep(approve);
                loadSupervisorChallenges();
            }
        }

        async function openScanner(ctx) {
            activeContext = ctx;
            const modal = document.getElementById('scanner-modal');
            const video = document.getElementById('scanner-video');
            modal.style.display = 'flex';

            if (!('BarcodeDetector' in window)) {
                alert("Camera barcode reader not supported natively on this browser. Use Chrome on Android or enter barcode manually.");
                closeScanner();
                return;
            }

            try {
                videoStream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: "environment" } } });
                video.srcObject = videoStream;
                video.play();
                const detector = new BarcodeDetector({ formats: ['code_128', 'ean_13', 'ean_8', 'upc_a', 'qr_code'] });
                const poller = setInterval(async () => {
                    if (modal.style.display === 'none') { clearInterval(poller); return; }
                    try {
                        const codes = await detector.detect(video);
                        if (codes.length > 0) {
                            const val = codes[0].rawValue;
                            beep(true);
                            if (activeContext === 'gun') {
                                document.getElementById('gun-last-scan').textContent = `Gun Sent: ${val} (${new Date().toLocaleTimeString()})`;
                                fetch(`${baseUrl}/api/pos/scan`, {
                                    method: 'POST',
                                    headers: { 'Content-Type': 'application/json' },
                                    body: JSON.stringify({ barcode: val, operator: "Floor Handset" })
                                });
                            } else {
                                closeScanner();
                                if (activeContext === 'dispense') {
                                    document.getElementById('drug-search').value = val;
                                    filterCatalog(val);
                                } else {
                                    document.getElementById('audit-sku').value = val;
                                    document.getElementById('audit-qty').focus();
                                }
                            }
                        }
                    } catch(e) {}
                }, 250);
            } catch(err) {
                alert("Camera error: " + err.message);
                closeScanner();
            }
        }

        function closeScanner() {
            document.getElementById('scanner-modal').style.display = 'none';
            if (videoStream) { videoStream.getTracks().forEach(t => t.stop()); videoStream = null; }
        }

        setInterval(syncDelta, 3000);
        syncDelta();
    </script>
</body>
</html>
""";
        #endregion
    }

    #endregion

    #region 5. UDP Auto-Discovery Beacon Subsystem

    public sealed class LanDiscoveryBeacon
    {
        private readonly int _httpPort;
        private readonly string _stationName;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private UdpClient? _udpClient;

        public LanDiscoveryBeacon(int httpPort, string stationName)
        {
            _httpPort = httpPort;
            _stationName = stationName;
        }

        public void Start() => Task.Run(() => BroadcastLoopAsync(_cts.Token));

        public void Stop()
        {
            _cts.Cancel();
            _udpClient?.Dispose();
        }

        private async Task BroadcastLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (_udpClient = new UdpClient())
                    {
                        _udpClient.EnableBroadcast = true;
                        var targetEp = new IPEndPoint(IPAddress.Broadcast, 45454);
                        var payload = new
                        {
                            service = "SSEDDUME_DSMS_STATION",
                            name = _stationName,
                            port = _httpPort,
                            time = DateTime.UtcNow.Ticks
                        };
                        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

                        while (!ct.IsCancellationRequested)
                        {
                            await _udpClient.SendAsync(bytes, bytes.Length, targetEp);
                            await Task.Delay(2500, ct);
                        }
                    }
                }
                catch
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(4000, ct);
                }
            }
        }
    }

    #endregion

    #region 6. Master Station GUI (Windows Forms Dashboard)

    public sealed class MasterStationDashboardForm : Form
    {
        private readonly SystemStateManager _state;
        private readonly MasterCompanionHttpEngine _httpServer;

        private ListView _stagedQueueList = null!;
        private ListView _inventoryTickerList = null!;
        private ListView _auditVarianceList = null!;
        private TextBox _txtBarcodeGunRegister = null!;
        private Label _lblServerStatus = null!;
        private System.Windows.Forms.Timer _refreshTimer = null!;

        public MasterStationDashboardForm(SystemStateManager state, MasterCompanionHttpEngine server)
        {
            _state = state;
            _httpServer = server;
            BuildComponentLayout();

            _state.OnRemoteBarcodeReceived += OnRemoteBarcodeInbound;
        }

        private void OnRemoteBarcodeInbound(RemoteGunScanEvent ev)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => OnRemoteBarcodeInbound(ev)));
                return;
            }

            _txtBarcodeGunRegister.Text = ev.Barcode;
            System.Media.SystemSounds.Asterisk.Play();
        }

        private void BuildComponentLayout()
        {
            this.Text = "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM - MASTER STATION (NDA CLASS C COMPLIANT)";
            this.Size = new Size(1180, 760);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Header Bar
            var topBar = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Color.FromArgb(30, 41, 59) };
            var lblBrand = new Label
            {
                Text = "SSEDDUME DRUG SHOP | Central Workstation & Dispense Terminal",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                Location = new Point(14, 14),
                AutoSize = true
            };
            topBar.Controls.Add(lblBrand);

            _lblServerStatus = new Label
            {
                Text = $"Port: {_httpServer.AssignedPort} | Discovery: 45454 Active | Workstation: {_state.StationMachineName}",
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(580, 18),
                AutoSize = true
            };
            topBar.Controls.Add(_lblServerStatus);
            this.Controls.Add(topBar);

            // Tab Control
            var mainTabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };

            // TAB 1: Live Checkout Register & Staged Prescriptions
            var tabCheckout = new TabPage("Floor Staged Tickets & POS [F1]") { BackColor = Color.FromArgb(15, 23, 42) };
            var splitPos = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 330,
                BackColor = Color.FromArgb(51, 65, 85)
            };

            var grpQueue = new GroupBox
            {
                Text = " Incoming Dispenser Prescription Tickets (Press [ENTER] or [F1] to Settle) ",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(226, 232, 240),
                BackColor = Color.FromArgb(15, 23, 42)
            };
            _stagedQueueList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None
            };
            _stagedQueueList.Columns.Add("Token", 80);
            _stagedQueueList.Columns.Add("Ticket ID", 90);
            _stagedQueueList.Columns.Add("Floor Staff", 120);
            _stagedQueueList.Columns.Add("Patient", 150);
            _stagedQueueList.Columns.Add("Lines", 70);
            _stagedQueueList.Columns.Add("Total (UGX)", 120);
            _stagedQueueList.Columns.Add("Time Staged", 130);
            _stagedQueueList.DoubleClick += (s, e) => FinalizeSelectedTicket();
            grpQueue.Controls.Add(_stagedQueueList);
            splitPos.Panel1.Controls.Add(grpQueue);

            var grpInv = new GroupBox
            {
                Text = " Live FEFO Expiry & Stock Safety Ticker ",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(226, 232, 240),
                BackColor = Color.FromArgb(15, 23, 42)
            };
            _inventoryTickerList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None
            };
            _inventoryTickerList.Columns.Add("Barcode", 130);
            _inventoryTickerList.Columns.Add("Product / Generic Form", 260);
            _inventoryTickerList.Columns.Add("Pack Breakdown", 130);
            _inventoryTickerList.Columns.Add("Total Units", 100);
            _inventoryTickerList.Columns.Add("Nearest Expiry", 120);
            _inventoryTickerList.Columns.Add("Retail / Unit (UGX)", 130);
            grpInv.Controls.Add(_inventoryTickerList);
            splitPos.Panel2.Controls.Add(grpInv);

            tabCheckout.Controls.Add(splitPos);
            mainTabs.TabPages.Add(tabCheckout);

            // TAB 2: Blind Stock Audit Reconciliations
            var tabAudit = new TabPage("Blind Audit Variance Journal") { BackColor = Color.FromArgb(15, 23, 42) };
            _auditVarianceList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None
            };
            _auditVarianceList.Columns.Add("SKU / Barcode", 140);
            _auditVarianceList.Columns.Add("Drug Name", 240);
            _auditVarianceList.Columns.Add("System Expected", 120);
            _auditVarianceList.Columns.Add("Physical Count", 120);
            _auditVarianceList.Columns.Add("Discrepancy Variance", 150);
            _auditVarianceList.Columns.Add("Auditor", 120);
            _auditVarianceList.Columns.Add("Timestamp", 140);
            tabAudit.Controls.Add(_auditVarianceList);
            mainTabs.TabPages.Add(tabAudit);

            this.Controls.Add(mainTabs);

            // Action Footer Bar
            var botPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = Color.FromArgb(30, 41, 59) };

            var lblGun = new Label
            {
                Text = "Active Gun / Remote Scan:",
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(12, 16),
                AutoSize = true
            };
            botPanel.Controls.Add(lblGun);

            _txtBarcodeGunRegister = new TextBox
            {
                Location = new Point(175, 12),
                Width = 160,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            botPanel.Controls.Add(_txtBarcodeGunRegister);

            var btnFinalize = new Button
            {
                Text = "F1: Settle & Print Receipt",
                Size = new Size(190, 34),
                Location = new Point(360, 8),
                BackColor = Color.FromArgb(2, 132, 199),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            btnFinalize.Click += (s, e) => FinalizeSelectedTicket();

            var btnPrintLabels = new Button
            {
                Text = "F2: Print Dosage Direction Labels",
                Size = new Size(240, 34),
                Location = new Point(560, 8),
                BackColor = Color.FromArgb(14, 165, 233),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            btnPrintLabels.Click += (s, e) => PrintLabelsForSelected();

            botPanel.Controls.Add(btnFinalize);
            botPanel.Controls.Add(btnPrintLabels);
            this.Controls.Add(botPanel);

            // Timer
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _refreshTimer.Tick += (s, e) => RefreshDashboardData();
            _refreshTimer.Start();

            this.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F1) FinalizeSelectedTicket();
                if (e.KeyCode == Keys.F2) PrintLabelsForSelected();
                if (e.KeyCode == Keys.F5) RefreshDashboardData();
            };

            RefreshDashboardData();
        }

        private void RefreshDashboardData()
        {
            var pending = _state.GetPendingTickets();
            _stagedQueueList.Items.Clear();
            foreach (var t in pending)
            {
                var lvi = new ListViewItem(t.TokenNumber);
                lvi.SubItems.Add(t.TicketId);
                lvi.SubItems.Add(t.OperatorName);
                lvi.SubItems.Add(t.CustomerIdentifier);
                lvi.SubItems.Add(t.Items.Count.ToString());
                lvi.SubItems.Add(t.TotalUgx.ToString("N0"));
                lvi.SubItems.Add(t.CreatedAt.ToLocalTime().ToString("HH:mm:ss"));
                lvi.Tag = t;
                _stagedQueueList.Items.Add(lvi);
            }

            var items = _state.GetInventoryDelta(0, StaffRole.Owner);
            _inventoryTickerList.Items.Clear();
            foreach (var i in items)
            {
                var lvi = new ListViewItem(i.Barcode);
                lvi.SubItems.Add($"{i.BrandName} ({i.GenericName})");
                lvi.SubItems.Add($"{i.UnitsPerPack}s / {i.UnitsPerStrip}s");
                lvi.SubItems.Add(i.TotalAvailableUnits.ToString());

                var nearest = i.GetFefoOrderedBatches().FirstOrDefault();
                lvi.SubItems.Add(nearest != null ? nearest.ExpiryDate.ToString("MMM-yyyy") : "NO VALID LOT");
                lvi.SubItems.Add(i.RetailSingleUnitPriceUgx.ToString("N0"));

                if (i.TotalAvailableUnits <= i.ReorderThresholdUnits)
                    lvi.ForeColor = Color.FromArgb(248, 113, 113);

                _inventoryTickerList.Items.Add(lvi);
            }

            var audits = _state.GetAuditLog();
            _auditVarianceList.Items.Clear();
            foreach (var a in audits)
            {
                var lvi = new ListViewItem(a.Barcode);
                lvi.SubItems.Add(a.DrugName);
                lvi.SubItems.Add(a.ExpectedUnits.ToString());
                lvi.SubItems.Add(a.PhysicalUnits.ToString());

                lvi.SubItems.Add(a.VarianceUnits > 0 ? $"+{a.VarianceUnits}" : a.VarianceUnits.ToString());
                if (a.VarianceUnits < 0) lvi.ForeColor = Color.FromArgb(248, 113, 113);
                else if (a.VarianceUnits > 0) lvi.ForeColor = Color.FromArgb(52, 211, 153);

                lvi.SubItems.Add(a.AuditorName);
                lvi.SubItems.Add(a.Timestamp.ToLocalTime().ToString("dd-MMM HH:mm:ss"));
                _auditVarianceList.Items.Add(lvi);
            }
        }

        private void FinalizeSelectedTicket()
        {
            if (_stagedQueueList.SelectedItems.Count == 0)
            {
                MessageBox.Show("Please highlight a staged floor ticket to finalize.", "No Ticket Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var ticket = (StagedCartTicket)_stagedQueueList.SelectedItems[0].Tag;
            var res = _state.FinalizeDispense(ticket.TicketId, "Master Cashier");

            if (res.Success)
            {
                byte[] receipt = ThermalPrinterSpooler.GenerateSalesReceipt(ticket, "Master Cashier");
                ThermalPrinterSpooler.SendRawBytes("POS-80", receipt);

                MessageBox.Show($"{res.Message}\nCash received: {ticket.TotalUgx:N0} UGX\nReceipt spooled successfully.", "Transaction Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshDashboardData();
            }
            else
            {
                MessageBox.Show(res.Message, "Stock Deduction Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PrintLabelsForSelected()
        {
            if (_stagedQueueList.SelectedItems.Count == 0) return;
            var ticket = (StagedCartTicket)_stagedQueueList.SelectedItems[0].Tag;

            int count = 0;
            foreach (var item in ticket.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.DosageInstructions))
                {
                    byte[] sigBytes = ThermalPrinterSpooler.GenerateDosageAuxiliaryLabel(item, ticket.CustomerIdentifier);
                    ThermalPrinterSpooler.SendRawBytes("POS-80", sigBytes);
                    count++;
                }
            }
            MessageBox.Show($"Spool completed: {count} auxiliary dosage label(s) generated.", "Auxiliary Direction Labels", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    #endregion
}
```

---

### How to Compile & Run (30-Second Setup)

1. Create a project folder on your Windows PC (e.g., `C:\SseddumeDSMS\`).
2. Inside that folder, create a file named `SseddumeDrugShopMaster.csproj` containing:
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <OutputType>WinExe</OutputType>
       <TargetFramework>net8.0-windows</TargetFramework>
       <UseWindowsForms>true</UseWindowsForms>
       <Nullable>enable</Nullable>
       <LangVersion>12.0</LangVersion>
     </PropertyGroup>
   </Project>
   ```
3. Save the code above into `C:\SseddumeDSMS\Program.cs`.
4. Open your command terminal in that folder and run:
   ```cmd
   dotnet run -c Release
   ```
5. On any Android phone connected to the shop's Wi-Fi router, open Chrome and visit:
   ```
   http://<WINDOWS_PC_LAN_IP>:8080/
   ```
