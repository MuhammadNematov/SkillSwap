using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Blocks;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Sessions;
using SkillSwap.Application.Files;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Jobs;
using SkillSwap.Application.LiveKit;
using SkillSwap.Application.Matching;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Skills;

namespace SkillSwap.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

        services.AddScoped<AuthTokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<TwoFactorService>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<SessionService>();
        services.AddScoped<SkillCatalogService>();
        services.AddScoped<SkillCatalogAdminService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<MySkillsService>();
        services.AddScoped<UserBlockService>();
        services.AddScoped<MatchService>();
        services.AddScoped<ExchangeRequestService>();
        services.AddScoped<ExchangeRequestJobs>();
        services.AddScoped<ExchangeService>();
        services.AddScoped<ExchangeJobs>();
        services.AddScoped<INotificationPublisher, NotificationPublisher>();
        services.AddScoped<NotificationService>();
        services.AddScoped<NotificationJobs>();
        services.AddScoped<ChatService>();
        services.AddSingleton<ChatThrottle>();
        services.AddScoped<NewMessagesNotifier>();
        services.AddScoped<LessonSessionService>();
        services.AddScoped<VideoRoomService>();
        services.AddScoped<Moderation.ChatAccessPolicy>();
        services.AddScoped<Reviews.ReviewService>();
        services.AddScoped<Reviews.ReviewJobs>();
        services.AddScoped<Reviews.ReviewScheduler>();
        services.AddScoped<VideoAttendanceService>();
        services.AddScoped<Abstractions.ILiveKitEventProcessor>(sp => sp.GetRequiredService<VideoAttendanceService>());
        services.AddScoped<LessonSessionJobs>();
        services.AddScoped<VideoLessonJobs>();
        services.AddScoped<Dashboard.DashboardService>();
        services.AddScoped<Account.AccountDeletionService>();
        services.AddScoped<Account.DataExportService>();
        services.AddScoped<Account.DataExportJobs>();
        services.AddScoped<AttachmentService>();
        services.AddScoped<AvatarService>();
        services.AddScoped<AvatarUrls>();
        services.AddScoped<UnreadMessagesJobs>();
        services.AddScoped<SkillSuggestionService>();
        services.AddScoped<LiveKitWebhookService>();
        services.AddScoped<NoopJob>();

        return services;
    }
}
