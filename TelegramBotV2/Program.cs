using System.Reflection.Metadata.Ecma335;
using static System.Formats.Asn1.AsnWriter;
using static System.Net.WebRequestMethods;

namespace SimpleTGBot;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.IO;
using System.Data.SqlClient;
using System.Text.RegularExpressions;
using NAudio.Wave;
using NAudio;

using NAudio.Wave.SampleProviders;
using System.Reflection.PortableExecutable;
using NAudio.Dsp;
using Microsoft.VisualBasic;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Collections.Concurrent;

public class TelegramBot
{
    //private string gameMode = "normal";
    Dictionary<long, string> gameModes = new Dictionary<long, string>();

    private int answerOptionsCount = 4;

    //private List<int> messageIdsWithButtons = new List<int>();
    Dictionary<long, List<int>> messageIdsWithButtons = new Dictionary<long, List<int>>();


    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    public void ReduceAudioQuality(string inputPath, string outputPath, int newBitRate)
    {
        using (var reader = new AudioFileReader(inputPath))
        {
            var format = new WaveFormat(reader.WaveFormat.SampleRate, newBitRate, reader.WaveFormat.Channels);
            using (var conversionStream = new WaveFormatConversionStream(format, reader))
            {
                WaveFileWriter.CreateWaveFile(outputPath, conversionStream);
            }
        }
    }

    public void CreateRandomSegments(string inputPath, string outputPath)
    {
        var random = new Random();
        using (var reader = new AudioFileReader(inputPath))
        {
            using (var writer = new WaveFileWriter(outputPath, reader.WaveFormat))
            {
                var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond * 1]; // Буфер для 1 секунд аудио
                for (int i = 0; i < 5; i++) // Создаем 5 сегментов, чтобы получить общую продолжительность 5 секунд
                {
                    int randomStart = random.Next(0, (int)reader.Length - buffer.Length);
                    reader.Position = randomStart;
                    reader.Read(buffer, 0, buffer.Length);
                    writer.Write(buffer, 0, buffer.Length);
                }
            }
        }
    }



    public void ConvertMp3ToWav(string mp3File, string outputFile)
    {
        try
        {
            using (Mp3FileReader mp3 = new Mp3FileReader(mp3File))
            {
                using (WaveStream pcm = WaveFormatConversionStream.CreatePcmStream(mp3))
                {
                    using (WaveFileWriter writer = new WaveFileWriter(outputFile, pcm.WaveFormat))
                    {
                        byte[] buffer = new byte[1024];
                        int bytesRead;

                        while ((bytesRead = pcm.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, bytesRead);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при конвертации MP3 в WAV: {ex.Message}");
        }
    }

    public void TrimWavFile(string inPath, string outPath, TimeSpan cutFromStart, TimeSpan cutFromEnd)
    {
        try
        {
            byte[] audioData;
            WaveFormat waveFormat;

            using (WaveFileReader reader = new WaveFileReader(inPath))
            {
                int bytesPerMillisecond = reader.WaveFormat.AverageBytesPerSecond / 1000;

                int startPos = (int)cutFromStart.TotalMilliseconds * bytesPerMillisecond;
                startPos = startPos - startPos % reader.WaveFormat.BlockAlign;

                int endBytes = (int)cutFromEnd.TotalMilliseconds * bytesPerMillisecond;
                endBytes = endBytes - endBytes % reader.WaveFormat.BlockAlign;
                int endPos = (int)reader.Length - endBytes;

                int length = endPos - startPos;
                audioData = new byte[length];
                reader.Position = startPos;
                reader.Read(audioData, 0, length);

                waveFormat = reader.WaveFormat;
            }

            using (FileStream fs = System.IO.File.OpenWrite(outPath))
            {
                using (WaveFileWriter writer = new WaveFileWriter(fs, waveFormat))
                {
                    writer.Write(audioData, 0, audioData.Length);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при обрезке WAV-файла: {ex.Message}");
        }
    }

    private static void TrimWavFile(WaveFileReader reader, WaveFileWriter writer, int startPos, int endPos)
    {
        try
        {
            reader.Position = startPos;
            byte[] buffer = new byte[1024];
            while (reader.Position < endPos)
            {
                int bytesRequired = (int)(endPos - reader.Position);
                if (bytesRequired > 0)
                {
                    int bytesToRead = Math.Min(bytesRequired, buffer.Length);
                    int bytesRead = reader.Read(buffer, 0, bytesToRead);
                    if (bytesRead > 0)
                    {
                        writer.WriteData(buffer, 0, bytesRead);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при обрезке WAV-файла: {ex.Message}");
        }
    }
    private Track GetTrackFromDatabase(int trackId)
    {
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            using (SqlCommand command = new SqlCommand("SELECT * FROM Tracks WHERE TrackID = @TrackID", connection))
            {
                command.Parameters.AddWithValue("@TrackID", trackId);

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new Track
                        {
                            TrackID = reader.GetInt32(0),
                            Title = reader.GetString(1),
                            Author = reader.GetString(2),
                            FileName = $"{reader.GetString(2)} - {reader.GetString(1)}.mp3" // Генерируем имя файла
                        };
                    }
                }
            }
        }

        return null;
    }

    private int GetUserScoreFromDatabase(long userId)
    {
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            using (SqlCommand command = new SqlCommand("SELECT Score FROM UserScores WHERE UserId = @UserId", connection))
            {
                command.Parameters.AddWithValue("@UserId", userId);

                object result = command.ExecuteScalar();
                if (result != null)
                {
                    return (int)result;
                }
                else
                {
                    // Если пользователь еще не имеет счета, возвращаем 0
                    return 0;
                }
            }
        }
    }

    // Объявляем botClient на уровне класса
    private TelegramBotClient botClient;
    private CancellationToken cancellationToken;
    private Dictionary<long, bool> hasButtons = new Dictionary<long, bool>();

    private Dictionary<long, System.Timers.Timer> chatTimers = new Dictionary<long, System.Timers.Timer>();

    private Dictionary<long, System.Threading.Timer> activityCheckTimers = new Dictionary<long, System.Threading.Timer>();

    private async Task StartActivityCheckTimer(long chatId)
    {
        while (true)
        {
            await Task.Delay(60000); // Проверяем активность каждую минуту

            if (lastAnswerTimes.ContainsKey(chatId) && DateTime.UtcNow - lastAnswerTimes[chatId] > TimeSpan.FromMinutes(1))
            {
                // Пользователь не отвечал в течение 5 минут, останавливаем игру
                if (gameTimers.ContainsKey(chatId))
                {
                    gameTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite); // Останавливаем таймер
                    gameTimers[chatId].Dispose(); // Освобождаем ресурсы таймера
                    gameTimers.Remove(chatId); // Удаляем таймер из словаря

                    try
                    {
                        // Отправляем сообщение, что игра остановлена из-за неактивности
                        await botClient.SendTextMessageAsync(chatId, "Игра остановлена из-за неактивности");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка при отправке сообщения: {ex.Message}");
                    }
                }
            }
        }
    }

    private void UpdateUserScoreInDatabase(long userId, int newScore)
    {
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            using (SqlCommand command = new SqlCommand(@"
            IF EXISTS (SELECT * FROM UserScores WHERE UserId = @UserId)
                UPDATE UserScores SET Score = @Score WHERE UserId = @UserId
            ELSE
                INSERT INTO UserScores (UserId, Score) VALUES (@UserId, @Score)", connection))
            {
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@Score", newScore);

                command.ExecuteNonQuery();
            }
        }
    }

    // Токен TG-бота. Можно получить у @BotFather
    private const string BotToken = "6841730740:AAEDEXpKaieYSPU0mAHXb9Hn-YWfAf1iXlU";

    private Dictionary<long, int> userScores = new Dictionary<long, int>();



    /// Инициализирует и обеспечивает работу бота до нажатия клавиши Esc
    public async Task Run()
    {

        // Инициализируем наш клиент, передавая ему токен.
        botClient = new TelegramBotClient(BotToken);

        // Служебные вещи для организации правильной работы с потоками
        using CancellationTokenSource cts = new CancellationTokenSource();
        cancellationToken = cts.Token;

        // Разрешённые события, которые будет получать и обрабатывать наш бот.
        // Будем получать только сообщения. При желании можно поработать с другими событиями.
        ReceiverOptions receiverOptions = new ReceiverOptions()
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
        };

        // Привязываем все обработчики и начинаем принимать сообщения для бота
        botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: OnErrorOccured,
            receiverOptions: receiverOptions,
            cancellationToken: cts.Token
        );

        // Запускаем таймер проверки активности
        //StartActivityCheckTimer(chatId);

        // Проверяем что токен верный и получаем информацию о боте
        var me = await botClient.GetMeAsync(cancellationToken: cts.Token);
        Console.WriteLine($"Бот @{me.Username} запущен.\nДля остановки нажмите клавишу Esc...");

        // Ждём, пока будет нажата клавиша Esc, тогда завершаем работу бота
        while (Console.ReadKey().Key != ConsoleKey.Escape) { }

        // Отправляем запрос для остановки работы клиента.
        cts.Cancel();
    }


    //private string correctAnswer;



    public void ConvertWavToMp3(string wavFile, string outputFile)
    {
        try
        {
            using (WaveFileReader wav = new WaveFileReader(wavFile))
            {
                using (MediaFoundationReader mediaFoundationReader = new MediaFoundationReader(wavFile))
                {
                    MediaFoundationEncoder.EncodeToMp3(mediaFoundationReader, outputFile);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при конвертации WAV в MP3: {ex.Message}");
        }
    }


    //private Dictionary<long, List<int>> messageIdsWithButtonsTimer = new Dictionary<long, List<int>>();
    private async Task OnTimerElapsed(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        // Удаляем кнопки из предыдущего сообщения
        if (messageIdsWithButtons.ContainsKey(chatId))
        {
            foreach (var messageId in messageIdsWithButtons[chatId])
            {
                try
                {
                    await botClient.EditMessageReplyMarkupAsync(
                        chatId: chatId,
                        messageId: messageId,
                        replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton[][] { }) // пустая клавиатура
                    );
                }
                catch (ApiRequestException ex)
                {
                    // Обрабатываем исключения...
                }
            }
            messageIdsWithButtons[chatId].Clear(); // очищаем список, так как все кнопки были удалены
        }

        // Если для этого чата уже есть таймер, останавливаем его
        if (gameTimers.ContainsKey(chatId))
        {
            gameTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
            gameTimers[chatId].Dispose(); // Освобождаем ресурсы старого таймера
            gameTimers.Remove(chatId); // Удаляем таймер из словаря
        }

        // Если для этого чата уже запущен таймер проверки активности, останавливаем и удаляем его
        if (activityCheckTimers.ContainsKey(chatId))
        {
            activityCheckTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
            activityCheckTimers[chatId].Dispose();
            activityCheckTimers.Remove(chatId);
        }

        // Проверяем, активен ли пользователь, прежде чем начинать новый раунд
        if (lastAnswerTimes.ContainsKey(chatId) && DateTime.UtcNow - lastAnswerTimes[chatId] <= TimeSpan.FromMinutes(1))
        {
            // Запускаем новый раунд игры
            await StartNewRound(botClient, chatId, cancellationToken);
        }
    }

    // Объявляем словарь для отслеживания времени последнего ответа каждого пользователя
    private Dictionary<long, DateTime> lastAnswerTimes = new Dictionary<long, DateTime>();

    // Глобальный словарь для хранения правильных ответов для каждого чата
    Dictionary<long, string> correctAnswers = new Dictionary<long, string>();

    // Объявляем словарь таймеров на уровне класса
    private Dictionary<long, System.Threading.Timer> gameTimers = new Dictionary<long, System.Threading.Timer>();

    private async Task StartNewRound(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {


        /*// Проверяем, выбран ли режим игры
        if (!gameModes.ContainsKey(chatId))
        {
            await botClient.SendTextMessageAsync(chatId, "Пожалуйста, выберите режим игры перед началом нового раунда.\nВы можете использовать команды:\n/normal\n/hard\n/superhard\n", cancellationToken: cancellationToken);
            return;
        }*/



        var now = DateTime.UtcNow;
        if (messageIdsWithButtons.ContainsKey(chatId))
        {
            foreach (var messageId in messageIdsWithButtons[chatId])
            {
                try
                {
                    await botClient.EditMessageReplyMarkupAsync(
                        chatId: chatId,
                        messageId: messageId,
                        replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton[][] { }) // пустая клавиатура
                    );
                }
                catch (ApiRequestException ex)
                {
                    // Обрабатываем исключения...
                }
            }
            messageIdsWithButtons[chatId].Clear(); // очищаем список, так как все кнопки были удалены
        }

        // Получаем случайный трек из базы данных
        var randomTrack = GetRandomTrackFromDatabase();

        // Определение правильного ответа
        correctAnswers[chatId] = $"{randomTrack.Author} - {randomTrack.Title}"; // всегда включаем автора в правильный ответ

        // Создаем полный путь к файлу трека
        string musicFolder = @"..\..\..\..\..\TelegramBotV2\TelegramBotV2\MusicForTB";
        var songClip = Path.Combine(musicFolder, randomTrack.FileName);

        // Если режим игры установлен на 'hard' или 'superhard', обрезаем аудиофайл
        if (gameModes[chatId] == "hard" || gameModes[chatId] == "superhard")
        {
            // Преобразуем MP3 в WAV
            string wavSongClip = Path.Combine(musicFolder, $"{chatId}_temp.mp3"); // Используем ID чата для создания уникального имени файла

            ConvertMp3ToWav(songClip, wavSongClip);

            // Определяем общую длительность трека
            TimeSpan cutFromStart;
            TimeSpan cutFromEnd;

            using (WaveFileReader reader = new WaveFileReader(wavSongClip))
            {
                double totalSeconds = reader.TotalTime.TotalSeconds;

                // Определяем, где начать воспроизведение (в середине трека)
                cutFromStart = TimeSpan.FromSeconds(totalSeconds / 2);

                // Определяем, сколько времени воспроизводить
                cutFromEnd = TimeSpan.FromSeconds(totalSeconds - (totalSeconds / 2 + (gameModes[chatId] == "superhard" ? 10 : 15)));
            }

            // Обрезаем аудиофайл
            string tempSongClip = Path.Combine(musicFolder, $"{chatId}_temp.wav"); // Используем ID чата для создания уникального имени файла
            TrimWavFile(wavSongClip, tempSongClip, cutFromStart, cutFromEnd);

            // Если режим игры установлен на 'superhard', изменяем скорость воспроизведения
            if (gameModes[chatId] == "superhard")
            {

                string distortedSongClipWav = Path.Combine(musicFolder, $"{chatId}_distorted.wav");
                CreateRandomSegments(wavSongClip, distortedSongClipWav);
                songClip = distortedSongClipWav;
            }
            else
            {
                songClip = tempSongClip;
            }
        }

        string correctAnswer;   
        correctAnswer = $"{randomTrack.Author} - {randomTrack.Title}"; // всегда включаем автора в правильный ответ

        if (randomTrack.Author.Length >= 10)
        {
            correctAnswer = $"{randomTrack.Title}"; // включаем только название трека в правильный ответ
        }
        else
        {
            correctAnswer = $"{randomTrack.Author} - {randomTrack.Title}"; // включаем автора в правильный ответ
        }

        // Получаем случайные треки из базы данных для неправильных ответов
        // Для режима 'superhard' получаем 
        var wrongTracks = GetRandomTracksFromDatabase(gameModes[chatId] == "superhard" ? 11 : 3, randomTrack.TrackID);
        var wrongAnswers = wrongTracks.Select(t => t.Author.Length >= 10 ? $"{t.Title}" : $"{t.Author} - {t.Title}").ToArray(); // включаем автора в неправильные ответы, если имя автора не длиннее 15 символов

        // Создаем список всех вариантов ответа
        List<string> allAnswers = new List<string>();
        allAnswers.Add(correctAnswer);
        allAnswers.AddRange(wrongAnswers);

        // Перемешиваем варианты ответа
        Random rand = new Random();
        allAnswers = allAnswers.OrderBy(x => rand.Next()).ToList();

        // Создаем словарь для связи ответов и ID треков
        Dictionary<string, int> answerTrackIds = new Dictionary<string, int>();
        answerTrackIds[correctAnswer] = randomTrack.TrackID;
        for (int i = 0; i < wrongAnswers.Length; i++)
        {
            answerTrackIds[wrongAnswers[i]] = wrongTracks[i].TrackID;
        }

        // Создаем клавиатуру с вариантами ответа
        // Для режима 'superhard' создаем клавиатуру с 8 вариантами ответа
        var keyboardButtons = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < allAnswers.Count; i += 2)
        {
            var row = new[]
            {
            InlineKeyboardButton.WithCallbackData(allAnswers[i], allAnswers[i] == correctAnswer ? $"correct:{answerTrackIds[allAnswers[i]]}" : $"wrong:{answerTrackIds[allAnswers[i]]}"),
            i + 1 < allAnswers.Count ? InlineKeyboardButton.WithCallbackData(allAnswers[i + 1], allAnswers[i + 1] == correctAnswer ? $"correct:{answerTrackIds[allAnswers[i + 1]]}" : $"wrong:{answerTrackIds[allAnswers[i + 1]]}") : null
        }.Where(b => b != null).ToArray();
            keyboardButtons.Add(row);
        }
        var keyboard = new InlineKeyboardMarkup(keyboardButtons);

        //correctAnswer = $"{randomTrack.Author} - {randomTrack.Title}"; // всегда включаем автора в правильный ответ


        // Отправляем аудиосообщение пользователю с вариантами ответа
        using (var stream = new FileStream(songClip, FileMode.Open, FileAccess.Read))
        {
            var sentMessage = await botClient.SendAudioAsync(
                chatId: chatId,
                audio: new InputFileStream(stream),
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);

            // Проверяем, есть ли уже список идентификаторов сообщений для этого чата
            if (!messageIdsWithButtons.ContainsKey(chatId))
            {
                // Если нет, создаем новый список
                messageIdsWithButtons[chatId] = new List<int>();
            }

            // Добавляем идентификатор сообщения в список
            messageIdsWithButtons[chatId].Add(sentMessage.MessageId);
        }

        // Если режим игры установлен на 'hard' или 'superhard', устанавливаем таймер
        if (gameModes[chatId] == "hard" || gameModes[chatId] == "superhard")
        {
            int timeLeft = gameModes[chatId] == "hard" ? 20 : 15; // Если режим 'hard', то время 20 секунд, иначе 15 секунд

            // Отправляем сообщение с обратным отсчетом времени
            var countdownMessage = await botClient.SendTextMessageAsync(chatId, $"Осталось времени: {timeLeft} секунд", cancellationToken: cancellationToken);

            // Если для этого чата уже есть таймер, останавливаем его
            if (gameTimers.ContainsKey(chatId))
            {
                gameTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
                gameTimers[chatId].Dispose(); // Освобождаем ресурсы старого таймера
                gameTimers.Remove(chatId); // Удаляем старый таймер из словаря
            }

            // Создаем новый таймер
            System.Threading.Timer gameTimer = null; // Объявляем таймер

            // Устанавливаем таймер на 1 секунду
            gameTimer = new System.Threading.Timer(async (e) =>
            {
                timeLeft--;
                if (timeLeft >= 0)
                {
                    // Обновляем сообщение с обратным отсчетом времени
                    await botClient.EditMessageTextAsync(chatId, countdownMessage.MessageId, $"Осталось времени: {timeLeft} секунд", cancellationToken: cancellationToken);
                }
                else
                {
                    // Время вышло, останавливаем таймер
                    gameTimer.Change(Timeout.Infinite, Timeout.Infinite);

                    // Удаляем сообщение с обратным отсчетом времени
                    await botClient.DeleteMessageAsync(chatId, countdownMessage.MessageId, cancellationToken);

                    // Получаем текущий счет пользователя
                    int userScore = GetUserScoreFromDatabase(chatId);

                    // Отправляем сообщение с правильным ответом, количеством баллов за раунд и текущим счетом пользователя
                    await botClient.SendTextMessageAsync(chatId, $"Время вышло!\nПравильный ответ был:\n`{correctAnswers[chatId]}`.\nВы получили +0 баллов.\nВаш текущий счет: {userScore}", cancellationToken: cancellationToken);

                    // Начинаем новый раунд
                    await OnTimerElapsed(botClient, chatId, cancellationToken);
                }
            }, null, 0, 1000);

            // Добавляем новый таймер в словарь
            gameTimers[chatId] = gameTimer;

            
        }
    }

    Dictionary<long, string> lastUserAnswers = new Dictionary<long, string>();


    Dictionary<long, bool> userHasAnswered = new Dictionary<long, bool>();

    private async Task OnCallbackQueryReceived(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        var callbackQuery = update.CallbackQuery;
        var callbackData = callbackQuery.Data;
        var chatId = callbackQuery.Message.Chat.Id;
        var userId = callbackQuery.From.Id; // Получаем ID пользователя

        // Проверяем, нажал ли пользователь уже кнопку
        if (userHasAnswered.ContainsKey(userId) && userHasAnswered[userId])
        {
            // Пользователь уже нажал кнопку, игнорируем его ответ
            return;
        }

        // Запоминаем, что пользователь нажал кнопку
        userHasAnswered[userId] = true;

        // Если для этого чата уже запущен таймер проверки активности, останавливаем и удаляем его
        if (activityCheckTimers.ContainsKey(chatId))
        {
            activityCheckTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
            activityCheckTimers[chatId].Dispose();
            activityCheckTimers.Remove(chatId);
        }

        // Запускаем новый таймер проверки активности для этого чата
        StartActivityCheckTimer(chatId);
        lastAnswerTimes[chatId] = DateTime.UtcNow;


        // Разделяем данные обратного вызова на тип ответа и ID трека
        var parts = callbackData.Split(':');
        var answerType = parts[0];
        var answerTrackId = int.Parse(parts[1]);

        // Получаем данные трека из базы данных
        var track = GetTrackFromDatabase(answerTrackId);

        // Получаем текущий счет пользователя
        int currentScore = GetUserScoreFromDatabase(userId); // Используем ID пользователя

        // Пользователь ответил на вопрос, останавливаем таймер
        if (gameTimers.ContainsKey(chatId))
        {
            gameTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
        }

        var isCorrect = answerType == "correct";
        if (isCorrect)
        {
            // Пользователь выбрал правильный ответ, увеличиваем его счет
            currentScore += gameModes[chatId] == "hard" ? 3 : gameModes[chatId] == "superhard" ? 6 : 1;
            UpdateUserScoreInDatabase(userId, currentScore); // Используем ID пользователя
        }

        if (callbackQuery.Message.ReplyMarkup != null && callbackQuery.Message.ReplyMarkup.InlineKeyboard != null && callbackQuery.Message.ReplyMarkup.InlineKeyboard.Any())
        {
            await Task.Delay(1000); // Задержка в 1 секунду
            var emptyKeyboard = new InlineKeyboardMarkup(new InlineKeyboardButton[][] { });

            // Проверяем, принадлежит ли сообщение боту
            if (callbackQuery.Message.From.Id == botClient.BotId)
            {
                try
                {
                    await botClient.EditMessageReplyMarkupAsync(
                        chatId: callbackQuery.Message.Chat.Id,
                        messageId: callbackQuery.Message.MessageId,
                        replyMarkup: emptyKeyboard,
                        cancellationToken: cancellationToken
                    );
                }
                catch (ApiRequestException ex)
                {
                    if (ex.ErrorCode != 400 || !ex.Message.Contains("message is not modified"))
                    {
                        // Если это не ошибка "message is not modified", пробрасываем исключение дальше
                        throw;
                    }
                    // Если это ошибка "message is not modified", просто игнорируем ее
                }
            }
        }

        if (correctAnswers.ContainsKey(chatId))
        {
            // Отправляем сообщение пользователю с результатом
            await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: $"Правильный ответ был:\n`{correctAnswers[chatId]}`\n" + // используем сохраненный правильный ответ
                  (isCorrect
                    ? $"*Вы угадали!* {(gameModes[chatId] == "hard" ? "+3" : gameModes[chatId] == "superhard" ? "+6" : "+1")} балл(ов) на ваш счёт\nТекущий счет: {currentScore}"
                    : $"*Увы!* Угадаете в другой раз)\nТекущий счет: {currentScore}"),
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
        }
        else
        {
            // Обрабатываем ситуацию, когда для данного chatId еще не было установлено значение
        }
        // После окончания раунда сбрасываем флаг
        userHasAnswered[userId] = false;

        await StartNewRound(botClient, chatId, cancellationToken);
    }

    //ТОП 10
    private List<(long UserId, string Nickname, int Score)> GetTopScoresFromDatabase()
    {
        var topScores = new List<(long UserId, string Nickname, int Score)>();

        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            using (SqlCommand command = new SqlCommand("SELECT TOP 10 UserScores.UserId, UserNicknames.Nickname, UserScores.Score FROM UserScores LEFT JOIN UserNicknames ON UserScores.UserId = UserNicknames.UserId ORDER BY Score DESC", connection))
            {
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long userId = reader.GetInt64(0);
                        string nickname = reader.IsDBNull(1) ? null : reader.GetString(1);
                        int score = reader.GetInt32(2);

                        topScores.Add((userId, nickname, score));
                    }
                }
            }
        }

        return topScores;
    }

    private void SetUserNicknameInDatabase(long userId, string nickname)
    {
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            using (SqlCommand command = new SqlCommand(@"
        IF EXISTS (SELECT * FROM UserNicknames WHERE UserId = @UserId)
            UPDATE UserNicknames SET Nickname = @Nickname WHERE UserId = @UserId
        ELSE
            INSERT INTO UserNicknames (UserId, Nickname) VALUES (@UserId, @Nickname)", connection))
            {
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@Nickname", nickname);

                command.ExecuteNonQuery();
            }
        }
    }

    // Объявляем словарь для отслеживания настроек приватности каждого пользователя
private Dictionary<long, bool> userPrivacySettings = new Dictionary<long, bool>();

    private Dictionary<long, User> userDatabase = new Dictionary<long, User>();

    private bool IsNewUser(long chatId)
    {
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            using (SqlCommand command = new SqlCommand("SELECT * FROM UserScores WHERE UserId = @UserId", connection))
            {
                command.Parameters.AddWithValue("@UserId", chatId);

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return false;
                    }
                }
            }

            // Если пользователя нет в базе данных, добавляем его
            using (SqlCommand insertCommand = new SqlCommand("INSERT INTO UserScores (UserId, Score) VALUES (@UserId, 0)", connection))
            {
                insertCommand.Parameters.AddWithValue("@UserId", chatId);
                insertCommand.ExecuteNonQuery();
                userPrivacySettings[chatId] = true;
            }

            return true;
        }
    }

    Dictionary<long, bool> activeRounds = new Dictionary<long, bool>();

    /// Обработчик события получения сообщения.
    async Task OnMessageReceived(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message is null || message.Text is not { } messageText)
        {
            return;
        }

        var chatId = message.Chat.Id;
        Console.WriteLine($"Received a message in chat {chatId}: '{messageText}'");

        // Если для этого чата уже запущен таймер проверки активности, останавливаем и удаляем его
        if (activityCheckTimers.ContainsKey(chatId))
        {
            activityCheckTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
            activityCheckTimers[chatId].Dispose();
            activityCheckTimers.Remove(chatId);
        }

        // Запускаем новый таймер проверки активности для этого чата
        StartActivityCheckTimer(chatId);

        lastAnswerTimes[chatId] = DateTime.UtcNow;

        // Проверяем, является ли это первое сообщение от пользователя
        if (IsNewUser(chatId))
        {
            // Отправляем приветственное сообщение
            await botClient.SendTextMessageAsync(chatId, "Привет!\nДобро пожаловать в игру\n'Угадай мелодию'\nВы можете узнать подробнее о командах, отправив команду /help.", cancellationToken: cancellationToken);
        }

        if (messageText == "/about")
        {
            // Отправляем сообщение с описанием бота
            await botClient.SendTextMessageAsync(chatId, "О боте:\nЭтот бот предназначен для игры в 'Угадай мелодию'.\nВы будете слушать музыкальные треки и угадывать их названия.\nУдачи!\n\nОб авторе:\nПусть будет это\n\nYouTube: \nhttps://www.youtube.com/@m1kraze0/shorts\n\nTikTok:\nhttps://www.tiktok.com/@m1kraze_?is_from_webapp=1&sender_device=pc\n", cancellationToken: cancellationToken);
        }
        else if (message.Text == "/stop")
        {
            // Пользователь отправил команду /stop, останавливаем таймер
            if (gameTimers.ContainsKey(chatId))
            {
                gameTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
                gameTimers.Remove(chatId);
            }

            // Удаляем прошлые кнопки, если они есть
            if (messageIdsWithButtons.ContainsKey(chatId))
            {
                foreach (var messageId in messageIdsWithButtons[chatId])
                {
                    try
                    {
                        await botClient.EditMessageReplyMarkupAsync(
                            chatId: chatId,
                            messageId: messageId,
                            replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton[][] { }) // пустая клавиатура
                        );
                    }
                    catch (ApiRequestException ex)
                    {
                        // Обрабатываем исключения...
                    }
                }
                messageIdsWithButtons[chatId].Clear(); // очищаем список, так как все кнопки были удалены
            }

            // Отправляем сообщение, что игра остановлена
            await botClient.SendTextMessageAsync(chatId, "Игра остановлена", cancellationToken: cancellationToken);

            // Сбрасываем маркер активного раунда
            activeRounds[chatId] = false;
        }
        else if (messageText == "/normal")
        {
            gameModes[chatId] = "normal";
            if (gameTimers.ContainsKey(chatId))
            {
                gameTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
                gameTimers.Remove(chatId);
            }
            // Удаляем прошлые кнопки, если они есть
            if (messageIdsWithButtons.ContainsKey(chatId))
            {
                foreach (var messageId in messageIdsWithButtons[chatId])
                {
                    try
                    {
                        await botClient.EditMessageReplyMarkupAsync(
                            chatId: chatId,
                            messageId: messageId,
                            replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton[][] { }) // пустая клавиатура
                        );
                    }
                    catch (ApiRequestException ex)
                    {
                        // Обрабатываем исключения...
                    }
                }
                messageIdsWithButtons[chatId].Clear(); // очищаем список, так как все кнопки были удалены
            }
            await botClient.SendTextMessageAsync(chatId, "Режим игры установлен на 'normal'", cancellationToken: cancellationToken);
            //await StartNewRound(botClient, chatId, cancellationToken); // Запускаем новый раунд
        }
        else if (messageText == "/hard")
        {
            gameModes[chatId] = "hard";
            if (gameTimers.ContainsKey(chatId))
            {
                gameTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
                gameTimers.Remove(chatId);
            }
            // Удаляем прошлые кнопки, если они есть
            if (messageIdsWithButtons.ContainsKey(chatId))
            {
                foreach (var messageId in messageIdsWithButtons[chatId])
                {
                    try
                    {
                        await botClient.EditMessageReplyMarkupAsync(
                            chatId: chatId,
                            messageId: messageId,
                            replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton[][] { }) // пустая клавиатура
                        );
                    }
                    catch (ApiRequestException ex)
                    {
                        // Обрабатываем исключения...
                    }
                }
                messageIdsWithButtons[chatId].Clear(); // очищаем список, так как все кнопки были удалены
            }
            await botClient.SendTextMessageAsync(chatId, "Режим игры установлен на 'hard'", cancellationToken: cancellationToken);
            //await StartNewRound(botClient, chatId, cancellationToken); // Запускаем новый раунд

        }
        else if (messageText == "/superhard")
        {
            gameModes[chatId] = "superhard";
            if (gameTimers.ContainsKey(chatId))
            {
                gameTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
                gameTimers.Remove(chatId);
            }
            if (messageIdsWithButtons.ContainsKey(chatId))
            {
                foreach (var messageId in messageIdsWithButtons[chatId])
                {
                    try
                    {
                        await botClient.EditMessageReplyMarkupAsync(
                            chatId: chatId,
                            messageId: messageId,
                            replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton[][] { }) // пустая клавиатура
                        );
                    }
                    catch (ApiRequestException ex)
                    {
                        // Обрабатываем исключения...
                    }
                }
                messageIdsWithButtons[chatId].Clear(); // очищаем список, так как все кнопки были удалены
            }
            await botClient.SendTextMessageAsync(chatId, "Режим игры установлен на 'superhard'", cancellationToken: cancellationToken);
            //await StartNewRound(botClient, chatId, cancellationToken); // Запускаем новый раунд
        }
        else if (messageText == "/score")
        {
            // Получаем текущий счет пользователя и отправляем ему сообщение с этой информацией
            int currentScore = GetUserScoreFromDatabase(chatId);
            await botClient.SendTextMessageAsync(chatId, $"Ваш текущий счет: {currentScore}", cancellationToken: cancellationToken);
        }
        else if (message.Text == "/start")
        {
            // Проверяем, начался ли уже раунд и выбран ли режим игры
            if ((activeRounds.ContainsKey(chatId) && activeRounds[chatId]) || !gameModes.ContainsKey(chatId))
            {
                string messageText1 = "Пожалуйста, выберите режим игры перед началом нового раунда.\nВы можете использовать команды:\n/normal\n/hard\n/superhard\n";
                if (activeRounds.ContainsKey(chatId) && activeRounds[chatId])
                {
                    messageText1 = "Игра уже началась!";
                }
                await botClient.SendTextMessageAsync(chatId, messageText1, cancellationToken: cancellationToken);
            }
            else
            {
                await StartNewRound(botClient, chatId, cancellationToken);
                activeRounds[chatId] = true; // Устанавливаем маркер активного раунда
            }
        }

        
        /*else if (message.Text == "/start")
        {
            // Пользователь отправил команду /start, начинаем новую игру
            if (gameTimers.ContainsKey(chatId))
            {
                gameTimers[chatId].Change(Timeout.Infinite, Timeout.Infinite);
                gameTimers.Remove(chatId);
            }

            // Начинаем новую игру
            await StartNewRound(botClient, chatId, cancellationToken);
        }*/
        else if (messageText.StartsWith("/name "))
        {
            // Получаем никнейм пользователя и устанавливаем его в базе данных
            string nickname = FilterNickname(messageText.Substring(6).Trim());
            if (nickname.Length <= 10)
            {
                SetUserNicknameInDatabase(chatId, nickname);
                await botClient.SendTextMessageAsync(chatId, $"Ваш никнейм был установлен на '{nickname}'", cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "Ваш никнейм должен быть не длиннее 10 символов", cancellationToken: cancellationToken);
            }
        }
        else if (messageText == "/private_name")
        {
            // Пользователь отправил команду /private_name, устанавливаем его настройки приватности в 'true'
            userPrivacySettings[chatId] = true;
            await botClient.SendTextMessageAsync(chatId, "Ваш профиль теперь скрыт", cancellationToken: cancellationToken);
        }
        else if (messageText == "/public_name")
        {
            // Пользователь отправил команду /public_name, устанавливаем его настройки приватности в 'false'
            userPrivacySettings[chatId] = false;
            await botClient.SendTextMessageAsync(chatId, "Ваш профиль теперь виден", cancellationToken: cancellationToken);
        }
        else if (messageText == "/top")
        {
            // Получаем топ 10 игроков и отправляем их пользователю
            var topScores = GetTopScoresFromDatabase();
            if (userPrivacySettings[chatId] == false)
            {
                var topScoresText = string.Join("\n", topScores.Select((item, index) => $"{index + 1}. {(string.IsNullOrEmpty(item.Nickname) ? $"[{item.UserId}](tg://user?id={item.UserId})" : $"[{item.Nickname}](tg://user?id={item.UserId})")}: {item.Score} points"));
                await botClient.SendTextMessageAsync(chatId, $"Топ 10 игроков:\n{topScoresText}", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            }
            else
            {
                var topScoresText = string.Join("\n", topScores.Select((item, index) => $"{index + 1}. {(string.IsNullOrEmpty(item.Nickname) ? $"{item.UserId}" : $"{item.Nickname}")}: {item.Score} points"));
                await botClient.SendTextMessageAsync(chatId, $"Топ 10 игроков:\n{topScoresText}", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            }
            //var topScoresText = string.Join("\n", topScores.Select((item, index) => $"{index + 1}. {(string.IsNullOrEmpty(item.Nickname) ? $"[{item.UserId}](tg://user?id={item.UserId})": $"[{item.Nickname}](tg://user?id={item.UserId})")}: {item.Score} points"));
            //await botClient.SendTextMessageAsync(chatId, $"Топ 10 игроков:\n{topScoresText}", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
        }
        /*else if (messageText == "/public_name")
        {
            // Получаем никнейм пользователя в Telegram и устанавливаем его в базе данных
            string telegramNickname = message.From.Username;
            if (telegramNickname != null && telegramNickname.Length <= 10)
            {
                SetUserNicknameInDatabase(chatId, telegramNickname);
                await botClient.SendTextMessageAsync(chatId, $"Ваш никнейм был установлен на '{telegramNickname}'", cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "Ваш никнейм в Telegram должен быть не длиннее 10 символов", cancellationToken: cancellationToken);
            }
        }*/
        else if (messageText == "/help")
        {
            var helpText = @"
- /start: Начинает новую игру
- /stop: Останавливает текущую игру
- /normal: Устанавливает режим игры на 'normal'. (1 балл за верный ответ + 4 варианта ответа + играет весь трек + нет ограничений по времени)
- /hard: Устанавливает режим игры на 'hard'. (3 балла за верный ответ + 4 варианта ответа + играет 15 секунд трека + таймер 20 секунд)
- /superhard: Устанавливает режим игры на 'superhard'. (6 баллов за верный ответ + 8 вариантов ответа + играет 5 секунд трека,где каждая секунда - рандомная часть трека + таймер 15 секунд)
- /score: Показывает ваш текущий счет
- /name [nickname]: Устанавливает ваш никнейм на указанный
- /public_name: Устанавливает профиль в публичном виде(ссылка на ваш профиль будет в Вашем Айди или Нике в топе)
- /private_name: Устанавливает профиль в приватном виде
- /top: Показывает топ 10 игроков
- /about: О боте и об авторе
";
            await botClient.SendTextMessageAsync(chatId, helpText, cancellationToken: cancellationToken);

            // В конце каждого раунда устанавливаем маркер активного раунда в 'false'
            activeRounds[chatId] = false;
        }



    }
    string FilterNickname(string input)
    {
        var disallowedCharacters = "+-=@#:$%^&*()[]{}|;:'\",.<>/?`~";
        return new string(input.Where(c => !disallowedCharacters.Contains(c)).ToArray());
    }

    private ConcurrentDictionary<long, ConcurrentQueue<Update>> messageQueues = new ConcurrentDictionary<long, ConcurrentQueue<Update>>();

    /*private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        long chatId;

        // Если обновление является сообщением, получаем Chat.Id и добавляем обновление в очередь сообщений
        if (update.Message != null)
        {
            chatId = update.Message.Chat.Id;

            if (!messageQueues.ContainsKey(chatId))
            {
                messageQueues[chatId] = new ConcurrentQueue<Update>();
            }
            messageQueues[chatId].Enqueue(update);
        }
        else if (update.CallbackQuery != null)
        {
            chatId = update.CallbackQuery.Message.Chat.Id;
        }
        else
        {
            // Обновление не является сообщением или обратным вызовом, пропускаем его
            return;
        }

        // Запускаем обработку каждого обновления в отдельном потоке
        _ = Task.Run(async () =>
        {
            try
            {
                // Обрабатываем сообщения из очереди
                while (messageQueues[chatId].TryDequeue(out Update nextUpdate))
                {
                    // Если следующее сообщение в очереди - это команда /start, пропускаем текущее сообщение
                    if (nextUpdate.Message?.Text == "/start" && messageQueues[chatId].Any(u => u.Message?.Text == "/start"))
                    {
                        continue;
                    }

                    if (nextUpdate.Type == UpdateType.Message)
                    {
                        // Обработка сообщений
                        await OnMessageReceived(botClient, nextUpdate, cancellationToken);
                    }
                    else if (nextUpdate.Type == UpdateType.CallbackQuery)
                    {
                        // Обработка обратных вызовов
                        await OnCallbackQueryReceived(botClient, nextUpdate, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                // Обработка ошибок
                Console.WriteLine(ex.Message);
            }
        });
    }*/

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        // Запускаем обработку каждого обновления в отдельном потоке
        _ = Task.Run(async () =>
        {
            try
            {
                if (update.Type == UpdateType.Message)
                {
                    // Обработка сообщений
                    await OnMessageReceived(botClient, update, cancellationToken);
                }
                else if (update.Type == UpdateType.CallbackQuery)
                {
                    // Обработка обратных вызовов
                    await OnCallbackQueryReceived(botClient, update, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // Обработка ошибок
                Console.WriteLine(ex.Message);
            }
        });
    }

    /// Обработчик исключений, возникших при работе бота
    Task OnErrorOccured(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        // В зависимости от типа исключения печатаем различные сообщения об ошибке
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",

            _ => exception.ToString()
        };

        Console.WriteLine(errorMessage);

        // Завершаем работу
        return Task.CompletedTask;
    }

    string connectionString = @"Server=localhost;Database=MusicDB;Trusted_Connection=True;";
    private Track GetRandomTrackFromDatabase()
    {
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            // Получаем количество треков в базе данных
            using (SqlCommand command = new SqlCommand("SELECT COUNT(*) FROM Tracks", connection))
            {
                int count = (int)command.ExecuteScalar();

                // Выбираем случайный индекс
                int index = new Random().Next(count);

                // Получаем трек по случайному индексу
                command.CommandText = $"SELECT * FROM Tracks ORDER BY TrackID OFFSET {index} ROWS FETCH NEXT 1 ROWS ONLY";
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new Track
                        {
                            TrackID = reader.GetInt32(0),
                            Title = reader.GetString(1),
                            Author = reader.GetString(2),
                            FileName = $"{reader.GetString(2)} - {reader.GetString(1)}.mp3" // Генерируем имя файла
                        };
                    }
                }
            }
        }

        return null;
    }

    private List<Track> GetRandomTracksFromDatabase(int count, int excludeTrackId)
    {
        List<Track> tracks = new List<Track>();

        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            // Получаем количество треков в базе данных
            using (SqlCommand command = new SqlCommand("SELECT COUNT(*) FROM Tracks WHERE TrackID != @ExcludeTrackID", connection))
            {
                command.Parameters.AddWithValue("@ExcludeTrackID", excludeTrackId);
                int total = (int)command.ExecuteScalar();

                // Выбираем случайные индексы
                var random = new Random();
                var indexes = Enumerable.Range(0, total).OrderBy(x => random.Next()).Take(count).ToList();

                // Получаем треки по случайным индексам
                foreach (var index in indexes)
                {
                    command.CommandText = $"SELECT * FROM Tracks WHERE TrackID != @ExcludeTrackID ORDER BY TrackID OFFSET {index} ROWS FETCH NEXT 1 ROWS ONLY";
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            tracks.Add(new Track
                            {
                                TrackID = reader.GetInt32(0),
                                Title = reader.GetString(1),
                                Author = reader.GetString(2)
                            });
                        }
                    }
                }
            }
        }

        return tracks;
    }
}

/*public class Track
{
    public int TrackID { get; set; }
    public string Title { get; set; } // Название трека
    public string Author { get; set; }
    public string FilePath { get; set; }
}*/
    public class Track
{
    public int TrackID { get; set; }
    public string Title { get; set; }
    public string Author { get; set; }
    public string FileName { get; set; } // Имя файла трека
}





public static class Program
{
    
    public static async Task Main(string[] args)
    {
        string musicFolder = @"..\..\..\..\..\TelegramBotV2\TelegramBotV2\MusicForTB";
        string connectionString = @"Server=localhost;Trusted_Connection=True;";

        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            // Проверка на существование базы данных и её удаление
            using (SqlCommand command = new SqlCommand(
                "IF EXISTS(SELECT 1 FROM sys.databases WHERE name = 'MusicDB') " +
                "ALTER DATABASE MusicDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE MusicDB;", connection))
            {
                command.ExecuteNonQuery();
            }

            // Создание новой базы данных
            using (SqlCommand command = new SqlCommand("CREATE DATABASE MusicDB;", connection))
            {
                command.ExecuteNonQuery();
            }
        

        // Выбор базы данных
        connection.ChangeDatabase("MusicDB");

            // Создание таблицы
            using (SqlCommand command = new SqlCommand(
                "CREATE TABLE Tracks (" +
                "TrackID INT IDENTITY(1,1) PRIMARY KEY, " +
                "TrackName NVARCHAR(255), " +
                "Author NVARCHAR(255), " +
                "FileName NVARCHAR(255));", connection)) // добавляем новое поле FileName
            {
                command.ExecuteNonQuery();
            }

            // Создание таблицы для хранения счета пользователей
            using (SqlCommand command = new SqlCommand(
                "CREATE TABLE UserScores (" +
                "UserId BIGINT PRIMARY KEY, " +
                "Score INT);", connection))
            {
                command.ExecuteNonQuery();
            }
        }


        Console.WriteLine("Database and table created successfully.");


        // Получаем все файлы mp3 в папке
        string[] files = Directory.GetFiles(musicFolder, "*.mp3");
        connectionString = @"Server=localhost;Database=MusicDB;Trusted_Connection=True;";





        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            foreach (string file in files)
            {
                // Разбираем название файла на название трека и автора
                string fileName = Path.GetFileNameWithoutExtension(file);
                string[] parts = fileName.Split('-');
                if (parts.Length != 2)
                {
                    Console.WriteLine($"Could not parse file name: {fileName}");
                    continue;
                }

                string author = parts[0].Trim();
                string trackName = parts[1].Trim();

                // Удаляем нежелательные строки из названия трека и автора
                trackName = Regex.Replace(trackName, @"\(\d+\)", "").Trim();
                author = Regex.Replace(author, @"\(\d+\)", "").Trim();

                // Добавляем трек в базу данных
                string query = "INSERT INTO Tracks (TrackName, Author, FileName) VALUES (@TrackName, @Author, @FileName)";
                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@TrackName", trackName);
                    command.Parameters.AddWithValue("@Author", author);
                    command.Parameters.AddWithValue("@FileName", fileName); // добавляем имя файла

                    command.ExecuteNonQuery();
                }
            }
        }

        






        // Создание таблицы для хранения никнеймов пользователей
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open(); // Открываем соединение

            using (SqlCommand command = new SqlCommand(
            "CREATE TABLE UserNicknames (" +
            "UserId BIGINT PRIMARY KEY, " +
            "Nickname NVARCHAR(10));", connection))
            {
                command.ExecuteNonQuery();
            }
        }

        //ДОБАВЛЕНИЕ НОВЫХ ТРЕКОВ БЕЗ ДУБЛИКАТОВ В БАЗУ ДАННЫХ БЕЗ ЕЁ ПЕРЕСОЗДАНИЯ
        //string[] files = Directory.GetFiles(musicFolder, "*.mp3");
        //string connectionString = @"Server=localhost;Database=MusicDB;Trusted_Connection=True;";
        /*using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();

            foreach (string file in files)
            {
                // Разбираем название файла на название трека и автора
                string fileName = Path.GetFileNameWithoutExtension(file);
                string[] parts = fileName.Split('-');
                if (parts.Length != 2)
                {
                    Console.WriteLine($"Could not parse file name: {fileName}");
                    continue;
                }

                string author = parts[0].Trim();
                string trackName = parts[1].Trim();

                // Удаляем нежелательные строки из названия трека и автора
                trackName = Regex.Replace(trackName, @"\(\d+\)", "").Trim();
                author = Regex.Replace(author, @"\(\d+\)", "").Trim();

                // Проверяем, существует ли уже трек в базе данных
                string checkQuery = "SELECT COUNT(*) FROM Tracks WHERE TrackName = @TrackName AND Author = @Author";
                using (SqlCommand command = new SqlCommand(checkQuery, connection))
                {
                    command.Parameters.AddWithValue("@TrackName", trackName);
                    command.Parameters.AddWithValue("@Author", author);

                    int existingCount = (int)command.ExecuteScalar();
                    if (existingCount > 0)
                    {
                        // Трек уже существует в базе данных, пропускаем его
                        continue;
                    }
                }

                // Добавляем трек в базу данных
                string insertQuery = "INSERT INTO Tracks (TrackName, Author, FileName) VALUES (@TrackName, @Author, @FileName)";
                using (SqlCommand command = new SqlCommand(insertQuery, connection))
                {
                    command.Parameters.AddWithValue("@TrackName", trackName);
                    command.Parameters.AddWithValue("@Author", author);
                    command.Parameters.AddWithValue("@FileName", fileName); // добавляем имя файла

                    command.ExecuteNonQuery();
                }
            }
        }*/

        TelegramBot telegramBot = new TelegramBot();
        await telegramBot.Run();

    }
}