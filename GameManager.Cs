using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using System.IO;
using Thirdweb;
using Thirdweb.Unity;
using UnityEngine.Networking;
using Firebase;
using Firebase.Database;
using Firebase.Extensions;

// --- DATA STRUCTURES ---

[System.Serializable]
public struct ClaimProof
{
    public string[] proof;
    public string quantityLimitPerWallet;
    public string pricePerToken;
    public string currency;
}

[System.Serializable]
public class QuestionData
{
    [TextArea]
    public string question;
    public string[] answers;
    public int correctAnswer; // Index 0-3
}

// Wrapper untuk parsing JSON dari Gemini
[System.Serializable]
public class QuestionListWrapper
{
    public List<QuestionData> questions;
}

[System.Serializable]
public class GeminiResponse
{
    public Candidate[] candidates;
}

[System.Serializable]
public class Candidate
{
    public Content content;
}

[System.Serializable]
public class Content
{
    public Part[] parts;
}

[System.Serializable]
public class Part
{
    public string text;
}

public class GameManager : MonoBehaviour
{
    [Header("AI Settings (Gemini 1.5 Pro)")]
    // [CENSORED] API Key has been masked for security in public repository.
    // Please replace with your own API Key from Google AI Studio.
    public string geminiApiKey = "AIza...bDbE";
    
    // [UPGRADE] Menggunakan Model Gemini 1.5 PRO (SOTA)
    private const string GEMINI_URL = "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-pro:generateContent";

    [Header("NFT Settings")]
    // [CENSORED] Smart Contract Address on Sepolia Testnet
    public string contractAddress = "0x1f...ab"; 
    public string chainId = "11155111"; // Sepolia
    public string tokenId = "0";
    // Metadata URI
    private string tokenUri = "ipfs://QmdHC3Q5PxbrDSFSzhMt12qDx8WcSkrVXyXNhcsDwXiK5t/0";

    [Header("Certificate Settings")]
    public Sprite certificateSprite;

    [Header("UI Panels")]
    public GameObject uiCamera;
    public GameObject gameWorld;
    public GameObject uiPanel;         // Menu Utama
    public GameObject quizPanel;       // Panel Kuis
    public GameObject certificatePanel;

    [Header("Quiz Elements")]
    public Button startQuizButton;
    public TextMeshProUGUI questionText;
    public Button[] answerButtons;     // Tombol Jawaban A-D
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI scoreText;
    public Button backButton;
    public Button nextButton;          // Disembunyikan (Auto-Next System)

    [Header("Certificate Elements")]
    public Image certImageDisplay;
    public Button closeButton;
    public Button downloadButton;
    public TextMeshProUGUI answerKeyText;

    [Header("Video Settings")]
    public Button videoButton;
    private string youtubeLink = "https://youtu.be/rt63X2j4Lis?si=G276EJX9uv2k2iqx";

    // --- INTERNAL VARIABLES ---
    private List<QuestionData> questions = new List<QuestionData>();
    private int currentQuestionIndex = 0;
    private int score = 0;
    private string walletAddress = "";
    private int[] userAnswers;
    
    // Timer Variables
    private float timePerQuestion = 120f; // 2 Menit
    private float currentTimer;
    private bool isTimerRunning = false;

    // Firebase
    private DatabaseReference dbReference;
    private bool isFirebaseReady = false;
    // [CENSORED] Firebase URL masked for privacy
    private string databaseURL = "https://game-kuis-******-default-rtdb.firebaseio.com/";

    // Control
    private bool isMonitoringWallet = false;

    void Start()
    {
        // 1. Setup UI Awal
        if (uiCamera) uiCamera.SetActive(true);
        if (gameWorld) gameWorld.SetActive(false);
        if (uiPanel) uiPanel.SetActive(false);
        if (quizPanel) quizPanel.SetActive(false);
        if (certificatePanel) certificatePanel.SetActive(false);
        if (scoreText) scoreText.gameObject.SetActive(false);
        if (answerKeyText) { answerKeyText.text = ""; answerKeyText.gameObject.SetActive(false); }

        // Sembunyikan tombol Next
        if (nextButton) nextButton.gameObject.SetActive(false);

        // 2. Inisialisasi System
        InitializeFirebase();
        MonitorWalletConnection();
        SetupButtonListeners();
        
        // Load Hardcoded (Soal Dasar Kelas 10)
        LoadHardcodedQuestions();
        
        LockCursor();
    }

    void Update()
    {
        // Logika Timer 2 Menit (Invisible)
        if (isTimerRunning)
        {
            currentTimer -= Time.deltaTime;
            
            // Jika waktu habis
            if (currentTimer <= 0)
            {
                currentTimer = 0;
                isTimerRunning = false;
                OnTimeRunOut();
            }
        }
    }

    private void SetupButtonListeners()
    {
        if (closeButton) closeButton.onClick.AddListener(CloseCertificatePanel);
        if (downloadButton) downloadButton.onClick.AddListener(OnDownloadButtonClicked);

        if (startQuizButton) startQuizButton.onClick.AddListener(CheckBalanceAndStart);
        if (videoButton) videoButton.onClick.AddListener(OpenVideoLink);
        
        if (backButton) backButton.onClick.AddListener(OnBackButtonClicked);

        for (int i = 0; i < answerButtons.Length; i++)
        {
            int index = i;
            answerButtons[index].onClick.RemoveAllListeners(); 
            answerButtons[index].onClick.AddListener(() => OnAnswerSelected(index));
        }
    }

    private void OnDestroy()
    {
        isMonitoringWallet = false;
    }

    // --- GEMINI 1.5 PRO GENERATION (High Reasoning) ---
    public void GenerateQuestionsWithAI()
    {
        UpdateStatus("Gemini Pro sedang menyusun soal...");
        StartCoroutine(FetchGeminiQuestions());
    }

    IEnumerator FetchGeminiQuestions()
    {
        // Prompt KHUSUS Penyetaraan Reaksi Kimia DASAR (Kelas 10)
        string prompt = "Buatkan 10 soal kimia dasar untuk kelas 10 SMA tentang Penyetaraan Reaksi Kimia Sederhana. " +
                        "Hindari senyawa kompleks. Fokus pada reaksi pembakaran sederhana, penggabungan, atau penggantian tunggal. " +
                        "Contoh tingkat kesulitan: H2 + O2 -> H2O atau N2 + H2 -> NH3. " +
                        "Format JSON harus valid dengan root object 'questions' berisi array. " +
                        "Setiap objek soal memiliki: 'question' (string), 'answers' (array string 4 opsi), dan 'correctAnswer' (int index 0-3). " +
                        "Jangan gunakan markdown, hanya raw JSON.";

        // Buat JSON Body
        string jsonBody = "{\"contents\":[{\"parts\":[{\"text\":\"" + prompt + "\"}]}]}";
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);

        using (UnityWebRequest request = new UnityWebRequest(GEMINI_URL + "?key=" + geminiApiKey, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("Gemini Response: " + request.downloadHandler.text);
                ParseGeminiResponse(request.downloadHandler.text);
            }
            else
            {
                Debug.LogError("Gemini Error: " + request.error);
                UpdateStatus("Gagal load AI. Menggunakan soal default.");
                LoadHardcodedQuestions(); // Fallback
                StartQuiz();
            }
        }
    }

    // --- REVISI: PARSING JSON LEBIH KUAT (PEMBERSIH TEKS) ---
    void ParseGeminiResponse(string jsonResponse)
    {
        try
        {
            GeminiResponse response = JsonUtility.FromJson<GeminiResponse>(jsonResponse);
            if (response.candidates != null && response.candidates.Length > 0)
            {
                string innerText = response.candidates[0].content.parts[0].text;
                
                // --- LOGIKA PEMBERSIH ---
                // Cari kurung kurawal pertama '{' dan terakhir '}'
                // Ini membuang teks basa-basi Gemini seperti "Here is the JSON..."
                int startIndex = innerText.IndexOf("{");
                int endIndex = innerText.LastIndexOf("}");

                if (startIndex >= 0 && endIndex > startIndex)
                {
                    // Potong teks hanya ambil JSON murni
                    innerText = innerText.Substring(startIndex, endIndex - startIndex + 1);
                }
                else
                {
                    throw new System.Exception("Tidak ditemukan format JSON di jawaban AI");
                }
                // -----------------------

                QuestionListWrapper wrapper = JsonUtility.FromJson<QuestionListWrapper>(innerText);
                
                if (wrapper != null && wrapper.questions != null && wrapper.questions.Count > 0)
                {
                    questions = wrapper.questions;
                    UpdateStatus("Soal Baru Siap!");
                    StartQuiz(); 
                }
                else
                {
                    throw new System.Exception("Isi soal kosong");
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("Parsing Error: " + e.Message);
            UpdateStatus("Error Parsing AI. Pakai soal default.");
            LoadHardcodedQuestions();
            StartQuiz();
        }
    }

    // --- LOGIKA DOWNLOAD & NFT ---
    public void OnDownloadButtonClicked()
    {
        if (downloadButton) downloadButton.interactable = false;
        DownloadCertificateImage();
        ClaimNFT();
    }

    public async void ClaimNFT()
    {
        UpdateStatus("Menyiapkan Transaksi...");
        try
        {
            var wallet = ThirdwebManager.Instance.GetActiveWallet();
            var address = await wallet.GetAddress();
            var contract = await ThirdwebManager.Instance.GetContract(contractAddress, BigInteger.Parse(chainId));

            ClaimProof proofData = new ClaimProof
            {
                proof = new string[0],
                quantityLimitPerWallet = "115792089237316195423570985008687907853269984665640564039457584007913129639935",
                pricePerToken = "0",
                currency = "0xEeeeeEeeeEeEeeEeEeEeeEEEeeeeEeeeeeeeEEeE"
            };

            var transaction = await contract.Prepare(wallet, "claim", 0, new object[] {
                    address, BigInteger.Parse(tokenId), 1, "0xEeeeeEeeeEeEeeEeEeEeeEEEeeeeEeeeeeeeEEeE", 0, proofData, new byte[0]
                });

            UpdateStatus("Cek Wallet untuk Konfirmasi!");
            await ThirdwebTransaction.SendAndWaitForTransactionReceipt(transaction);
            UpdateStatus("Sukses! NFT Masuk Wallet.");
        }
        catch (System.Exception e)
        {
            Debug.LogError("Gagal Claim: " + e); 
            if (e.Message.Contains("user rejected")) UpdateStatus("Transaksi Dibatalkan.");
            else UpdateStatus("Gagal Claim. Cek Console.");
        }
        if (downloadButton) downloadButton.interactable = true;
    }

    public void DownloadCertificateImage()
    {
        // LINK IPFS YANG BENAR (ASLI)
        string ipfsUrl = "ipfs://QmSNcWtDh1b7hg211ocFssoqX6zRY7hPGbMcqxHNFiGTDE/0.jpg"; 
        
        // Convert ke Gateway PINATA
        string finalUrl = ipfsUrl.Replace("ipfs://", "https://gateway.pinata.cloud/ipfs/");
        
        StartCoroutine(DownloadImageFromUrl(finalUrl, "Sertifikat_Kuis_Kimia"));
    }

    System.Collections.IEnumerator DownloadImageFromUrl(string url, string fileNameBase)
    {
        UpdateStatus("Sedang Mendownload...");
        using (UnityWebRequest uwr = UnityWebRequestTexture.GetTexture(url))
        {
            yield return uwr.SendWebRequest();
            if (uwr.result == UnityWebRequest.Result.Success)
            {
                Texture2D texture = DownloadHandlerTexture.GetContent(uwr);
                
                // Pastikan texture bisa dibaca untuk disimpan
                if (!texture.isReadable) {
                    Texture2D readableText = new Texture2D(texture.width, texture.height, texture.format, false);
                    readableText.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                    readableText.Apply();
                    texture = readableText;
                }
                
                byte[] bytes = texture.EncodeToPNG();
                string fileName = fileNameBase + "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";

                #if UNITY_ANDROID && !UNITY_EDITOR
                    NativeGallery.SaveImageToGallery(bytes, "MetaEdu Certificate", fileName, (success, path) => {
                        if (success) UpdateStatus("Sukses! Cek Galeri HP.");
                        else UpdateStatus("Gagal Simpan.");
                    });
                #else
                    string path = Path.Combine(Application.persistentDataPath, fileName);
                    File.WriteAllBytes(path, bytes);
                    UpdateStatus("Tersimpan di: " + path);
                #endif
            }
            else UpdateStatus("Gagal Download.");
        }
    }

    // --- FIREBASE ---
    void InitializeFirebase()
    {
        FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task => {
            if (task.Result == DependencyStatus.Available)
            {
                dbReference = FirebaseDatabase.GetInstance(FirebaseApp.DefaultInstance, databaseURL).RootReference;
                isFirebaseReady = true;
            }
        });
    }

    // --- REVISI: SIMPAN DENGAN TIMESTAMP (SEJARAH) ---
    void SaveToFirebase(int correct, int totalScore, bool status)
    {
        if (!isFirebaseReady || string.IsNullOrEmpty(walletAddress)) return;
        
        // Buat ID unik berdasarkan Waktu (biar tidak saling menimpa data lama)
        string timestampKey = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string dateReadable = System.DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm:ss");

        // Simpan di Folder: Quiz_Results_10 -> [Wallet Address] -> [Timestamp Unik]
        var data = new Dictionary<string, object> {
            { "correctCount", correct },
            { "totalScore", totalScore },
            { "isPassed", status },
            { "timestamp", dateReadable } 
        };

        // Ini akan membuat riwayat skor baru setiap kali main
        dbReference.Child("Quiz_Results_10").Child(walletAddress).Child(timestampKey).SetValueAsync(data);
    }

    // --- LOGIKA KUIS ---
    public async void CheckBalanceAndStart()
    {
        UpdateStatus("Mengecek Blockchain...");
        try
        {
            // Flow: Start dengan soal hardcoded dulu. 
            StartQuiz();
        } 
        catch { 
            Debug.LogWarning("Offline Mode"); 
            StartQuiz(); 
        }
    }

    void StartQuiz()
    {
        currentQuestionIndex = 0;
        score = 0;
        
        userAnswers = new int[questions.Count];
        for (int i = 0; i < userAnswers.Length; i++) userAnswers[i] = -1;

        if (uiPanel) uiPanel.SetActive(false);
        if (quizPanel) quizPanel.SetActive(true);
        if (backButton) backButton.gameObject.SetActive(true);
        if (answerKeyText) answerKeyText.gameObject.SetActive(false);
        
        ShowQuestion();
    }

    void ShowQuestion()
    {
        if (currentQuestionIndex >= questions.Count) return;

        // 1. Reset Timer 2 Menit (120 Detik)
        currentTimer = timePerQuestion; 
        isTimerRunning = true;

        QuestionData q = questions[currentQuestionIndex];
        questionText.text = q.question;
        
        for (int i = 0; i < answerButtons.Length; i++)
        {
            answerButtons[i].gameObject.SetActive(true);
            answerButtons[i].interactable = true;
            TextMeshProUGUI btnText = answerButtons[i].GetComponentInChildren<TextMeshProUGUI>();
            if (btnText != null) btnText.text = q.answers[i];
            answerButtons[i].image.color = Color.white;
        }

        UpdateStatus($"Soal {currentQuestionIndex + 1} / {questions.Count}");
    }

    // --- AUTO NEXT LOGIC ---
    void OnAnswerSelected(int index)
    {
        userAnswers[currentQuestionIndex] = index;
        
        // Visual Feedback sebentar (Opsional, sangat cepat)
        answerButtons[index].image.color = Color.yellow;

        // Langsung Next
        NextQuestion();
    }
    
    void NextQuestion()
    {
        currentQuestionIndex++;
        if (currentQuestionIndex < questions.Count)
        {
            ShowQuestion(); // Timer reset otomatis saat soal baru tampil
        }
        else
        {
            FinishQuiz();
        }
    }

    void OnTimeRunOut()
    {
        // Waktu habis, anggap tidak menjawab/lanjut
        NextQuestion();
    }

    void OnBackButtonClicked()
    {
        if (currentQuestionIndex > 0)
        { 
            currentQuestionIndex--;
            ShowQuestion(); 
            // Karena ShowQuestion dipanggil, timer reset ke 2 menit lagi
            // User bisa memilih jawaban ulang
        }
    }

    async void FinishQuiz()
    {
        isTimerRunning = false;
        if (backButton) backButton.gameObject.SetActive(false);
        for (int i = 0; i < answerButtons.Length; i++) answerButtons[i].gameObject.SetActive(false);

        int correctCount = 0;
        for (int i = 0; i < questions.Count; i++) 
        { 
            if (userAnswers[i] == questions[i].correctAnswer) correctCount++;
        }
        score = correctCount * 10;
        bool isPassed = score >= 70;

        SaveToFirebase(correctCount, score, isPassed);

        if (scoreText) {
            scoreText.gameObject.SetActive(true);
            scoreText.text = "Skor Akhir: " + score;
        }
        
        string resultMsg = isPassed ? "SELAMAT! ANDA LULUS." : "MAAF, ANDA GAGAL.";
        questionText.text = $"{resultMsg}\nSkor: {score}\n(Butuh 70 untuk Sertifikat)";

        // Tampilkan Kunci Jawaban
        if (answerKeyText)
        {
            string[] optionLabels = new string[] { "A", "B", "C", "D" };
            string generatedKey = "<margin-left=15%><b>KUNCI JAWABAN:</b>\n";
            for (int i = 0; i < questions.Count; i++)
            {
                int correctIdx = questions[i].correctAnswer;
                if(correctIdx >= 0 && correctIdx < 4)
                    generatedKey += $"{i + 1}. {optionLabels[correctIdx]} ";
                if ((i + 1) % 5 == 0) generatedKey += "\n";
            }
            generatedKey += "</margin>";
            answerKeyText.text = generatedKey;
            answerKeyText.gameObject.SetActive(true);
        }

        // Delay 5 detik sebelum pindah panel
        await Task.Delay(5000);
        
        if (quizPanel) quizPanel.SetActive(false);
        
        if (isPassed) 
        {
            ShowCertificate();
        }
        else 
        {
            // Jika gagal, otomatis generate soal baru pakai AI (Gemini 1.5 Pro)
            GenerateQuestionsWithAI(); 
            if (uiPanel) uiPanel.SetActive(true);
        }
    }

    // --- UTILITIES ---
    void ShowCertificate()
    {
        if (certificatePanel) certificatePanel.SetActive(true);
        if (certImageDisplay && certificateSprite) certImageDisplay.sprite = certificateSprite;
    }

    void LockCursor() { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
    void OpenVideoLink() { Application.OpenURL(youtubeLink); }
    public void CloseCertificatePanel() => certificatePanel.SetActive(false);
    void UpdateStatus(string msg) { if (statusText) statusText.text = msg; }

    private async void MonitorWalletConnection()
    {
        isMonitoringWallet = true;
        while (isMonitoringWallet)
        {
            try
            {
                var address = await ThirdwebManager.Instance.GetActiveWallet().GetAddress();
                if (!string.IsNullOrEmpty(address) && walletAddress != address)
                {
                    walletAddress = address;
                    if (uiCamera) uiCamera.SetActive(false);
                    if (gameWorld) gameWorld.SetActive(true);
                }
            } catch { }
            if (!isMonitoringWallet) break;
            await Task.Delay(1000);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player")) { if (uiPanel) uiPanel.SetActive(true); Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player")) { LockCursor(); if (uiPanel) uiPanel.SetActive(false); }
    }

    // Soal Penyetaraan Kimia Dasar Kelas 10 (Hardcoded Default)
    void LoadHardcodedQuestions()
    {
        questions.Clear();
        // 1. H2 + O2 -> H2O
        questions.Add(new QuestionData { 
            question = "Setarakan reaksi pembentukan air:\n... H2 + O2 -> 2 H2O", 
            answers = new string[] { "1", "2", "3", "4" }, 
            correctAnswer = 1 // 2 H2 + O2 -> 2 H2O
        });
        
        // 2. N2 + H2 -> NH3
        questions.Add(new QuestionData { 
            question = "Setarakan reaksi pembentukan amonia:\nN2 + ... H2 -> 2 NH3", 
            answers = new string[] { "1", "2", "3", "4" }, 
            correctAnswer = 2 // N2 + 3 H2 -> 2 NH3
        });

        // 3. Mg + O2 -> MgO
        questions.Add(new QuestionData { 
            question = "Reaksi magnesium dengan oksigen:\n2 Mg + O2 -> ... MgO", 
            answers = new string[] { "1", "2", "3", "4" }, 
            correctAnswer = 1 // 2 MgO
        });

        // 4. S + O2 -> SO2
        questions.Add(new QuestionData { 
            question = "Reaksi belerang dengan oksigen:\nS + O2 -> ...", 
            answers = new string[] { "SO", "SO2", "SO3", "S2O" }, 
            correctAnswer = 1 // SO2 (Sudah setara)
        });

        // 5. CH4 + O2 -> CO2 + H2O
        questions.Add(new QuestionData { 
            question = "Pembakaran metana:\nCH4 + ... O2 -> CO2 + 2 H2O", 
            answers = new string[] { "1", "2", "3", "4" }, 
            correctAnswer = 1 // CH4 + 2 O2
        });

        // 6. Na + Cl2 -> NaCl
        questions.Add(new QuestionData { 
            question = "Reaksi natrium dengan klorin:\n2 Na + Cl2 -> ... NaCl", 
            answers = new string[] { "1", "2", "3", "4" }, 
            correctAnswer = 1 // 2 NaCl
        });

        // 7. Fe + S -> FeS
        questions.Add(new QuestionData { 
            question = "Besi bereaksi dengan belerang:\nFe + S -> ...", 
            answers = new string[] { "FeS", "Fe2S", "FeS2", "Fe2S3" }, 
            correctAnswer = 0 // FeS
        });

        // 8. H2 + Cl2 -> HCl
        questions.Add(new QuestionData { 
            question = "Setarakan reaksi:\nH2 + Cl2 -> ... HCl", 
            answers = new string[] { "1", "2", "3", "4" }, 
            correctAnswer = 1 // 2 HCl
        });

        // 9. C + O2 -> CO (Pembakaran tak sempurna)
        questions.Add(new QuestionData { 
            question = "Setarakan reaksi:\n2 C + O2 -> ... CO", 
            answers = new string[] { "1", "2", "3", "4" }, 
            correctAnswer = 1 // 2 CO
        });

        // 10. Zn + HCl -> ZnCl2 + H2
        questions.Add(new QuestionData { 
            question = "Reaksi seng dengan asam klorida:\nZn + ... HCl -> ZnCl2 + H2", 
            answers = new string[] { "1", "2", "3", "4" }, 
            correctAnswer = 1 // Zn + 2 HCl
        });
    }
}
