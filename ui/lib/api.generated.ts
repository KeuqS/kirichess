// GENERATED FILE, DO NOT EDIT.
//
// Source of truth: docs/api/openapi.json, itself generated from the C# endpoints by
//                  `dotnet run --project src/KiriChess.Server -- --export-openapi docs/api/openapi.json`
// Regenerate:      npm run gen:api   (npm run gen:api:check verifies it in CI)
//
// Every type below is the shape the server actually serializes. Change the C# DTO, re-export the
// document, and regenerate: never edit this file, and never hand-write a mirror of it in types.ts.

export interface paths {
    "/api/games/live": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    limit?: number;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["LiveGameDto"][];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/challenges/{token}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    token: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ChallengeDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    token: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/seeks": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["OpenSeekDto"][];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{id}/replay": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ReplayResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{id}/pgn": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/x-chess-pgn": string;
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/ratings/me": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RatingDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/ratings/users/{userId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    userId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RatingDto"][];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/leaderboards/{pool}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    limit?: number;
                    includeProvisional?: boolean;
                };
                header?: never;
                path: {
                    pool: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["LeaderboardEntryDto"][];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/ratings/users/{userId}/history/{pool}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    limit?: number;
                };
                header?: never;
                path: {
                    userId: string;
                    pool: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RatingHistoryPointDto"][];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/users/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PublicProfileDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/users/{id}/avatar": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/reports": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["CreateReportRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ReportDto"];
                    };
                };
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ReportDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/reports/mine": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ReportDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/my-sanctions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["MySanctionDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/notifications/unsubscribe": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["UnsubscribeRequest"];
                };
            };
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/push/vapid-public-key": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["VapidPublicKeyDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/detect/sandbagging/{userId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    sinceDays?: number;
                    pool?: string;
                };
                header?: never;
                path: {
                    userId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DetectionReportDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/detect/sandbagging": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    sinceDays?: number;
                    minScore?: number;
                    take?: number;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DetectionReportDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/detect/pair-concentration/{userId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    sinceDays?: number;
                    pool?: string;
                };
                header?: never;
                path: {
                    userId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DetectionReportDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/detect/pair-concentration": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    sinceDays?: number;
                    minScore?: number;
                    take?: number;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DetectionReportDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/analytics/counter": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["CounterRequest"];
                };
            };
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/analytics/traffic": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    sinceDays?: number;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["TrafficSummaryDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/register": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["RegisterRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AuthResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/login": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["LoginRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AuthResponse"] | components["schemas"]["TwoFactorRequiredResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Locked */
                423: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/login/2fa": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["TwoFactorLoginRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AuthResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Locked */
                423: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/refresh": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["RefreshRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AuthResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/logout": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["LogoutRequest"];
                };
            };
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/logout-all": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/confirm-email": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ConfirmEmailRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ConfirmEmailResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/resend-confirmation": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ResendConfirmationRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SentResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/forgot-password": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ForgotPasswordRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SentResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/reset-password": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ResetPasswordRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ResetPasswordResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/external/google/start": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    returnPath?: string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description Found */
                302: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/external/callback": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    returnPath?: string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description Found */
                302: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/auth/external/exchange": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ExternalExchangeRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ExternalExchangeResponse"] | components["schemas"]["TwoFactorRequiredResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["CreateGameRequest"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GameStateResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/playing": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CurrentGameResponse"];
                    };
                };
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GameStateResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{id}/legal-moves": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": string[];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{id}/moves": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["MoveRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["MoveResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{id}/resign": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GameStateResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/mine": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    take?: number;
                    skip?: number;
                    before?: string;
                    beforeId?: string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GameHistoryEntryDto"][];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/challenges": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["CreateChallengeRequest"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ChallengeDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Too Many Requests */
                429: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/challenges/mine": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ChallengeDto"][];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/challenges/{token}/accept": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    token: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ChallengeAcceptedDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/reports": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    status?: string;
                    take?: number;
                    skip?: number;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ModerationReportDto"][];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/reports/{id}/resolve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ResolveReportRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ModerationReportDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/users/{userId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    userId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ModerationUserDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/sanctions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    activeOnly?: boolean;
                    userId?: string;
                    take?: number;
                    skip?: number;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SanctionDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["CreateSanctionRequest"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SanctionDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/sanctions/{id}/lift": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["LiftSanctionRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SanctionDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/users/{userId}/export": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    userId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AccountExportDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/users/{userId}/erase": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    userId: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["EraseAccountRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DeleteAccountResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/display-name/check": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    candidate?: string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DisplayNameVerdict"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/display-name": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ChangeDisplayNameRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DisplayNameResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/content/display-names": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    problemsOnly?: boolean;
                    take?: number;
                    skip?: number;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DisplayNameScanRowDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/content/display-names/{userId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    userId: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["ModeratorRenameRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DisplayNameResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/problem+json": components["schemas"]["HttpValidationProblemDetails"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/export": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AccountExportDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/delete": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["DeleteAccountRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DeleteAccountResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/sessions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SessionDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/sessions/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/api-tokens": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ApiTokenDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["CreateApiTokenRequest"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CreatedApiTokenDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/api-tokens/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/notifications": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["NotificationPreferencesDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["UpdateNotificationPreferenceRequest"];
                };
            };
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/avatar": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "multipart/form-data": {
                        file: components["schemas"]["IFormFile"];
                    };
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AvatarResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        post?: never;
        delete: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["AvatarResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/email": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["EmailStatusDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/push/subscribe": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["PushSubscribeRequest"];
                };
            };
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/push/unsubscribe": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["PushUnsubscribeRequest"];
                };
            };
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/donations/reference": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DonationReferenceDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/donations/me": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PatronStatusDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/2fa": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["TwoFactorStatusResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/2fa/enrol": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["TwoFactorEnrolmentResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/2fa/enable": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["TwoFactorEnableRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["TwoFactorEnableResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/2fa/disable": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["TwoFactorDisableRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["TwoFactorDisableResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/account/2fa/recovery-codes": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["TwoFactorCodeRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["TwoFactorRecoveryCodesResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/retention/dormant": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    inactiveDays?: number;
                    take?: number;
                    skip?: number;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DormantAccountScanDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/kill-switches": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": {
                            [key: string]: boolean;
                        };
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/kill-switches/{name}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    name: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/json": components["schemas"]["SetKillSwitchRequest"];
                };
            };
            responses: {
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/detect/engine/{userId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    sinceDays?: number;
                    maxGames?: number;
                };
                header?: never;
                path: {
                    userId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DetectionReportDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/moderation/detect/engine/game/{gameId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    gameId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DetectionReportDto"][];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/analytics/balance": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    sinceDays?: number;
                    ratedOnly?: boolean;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BalanceSummaryDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/analytics/retention": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    sinceDays?: number;
                    grain?: string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["RetentionSummaryDto"];
                    };
                };
                /** @description Unauthorized */
                401: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        AccountExportApiTokenDto: {
            name: string;
            tokenPrefix: string;
            scopes: string[];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            expiresAt: null | string;
            /** Format: date-time */
            revokedAt: null | string;
            /** Format: date-time */
            lastUsedAt: null | string;
        };
        AccountExportDto: {
            exportVersion: string;
            /** Format: date-time */
            generatedAt: string;
            profile: components["schemas"]["AccountExportProfileDto"];
            ratings: components["schemas"]["AccountExportRatingDto"][];
            ratingHistory: components["schemas"]["AccountExportRatingHistoryDto"][];
            games: components["schemas"]["AccountExportGameDto"][];
            reportsFiled: components["schemas"]["AccountExportReportDto"][];
            reportsAgainst: components["schemas"]["AccountExportReportAgainstDto"][];
            sanctions: components["schemas"]["AccountExportSanctionDto"][];
            sessions: components["schemas"]["AccountExportSessionDto"][];
            apiTokens: components["schemas"]["AccountExportApiTokenDto"][];
            truncation: components["schemas"]["AccountExportTruncationDto"];
        };
        AccountExportGameDto: {
            /** Format: uuid */
            gameId: string;
            colour: string;
            opponentDisplayName: string;
            /** @enum {string} */
            result: "Ongoing" | "WhiteWins" | "BlackWins" | "Draw" | "Aborted";
            /** @enum {string} */
            termination: "None" | "Checkmate" | "Flagfall" | "Stalemate" | "Resignation" | "DrawByRepetition" | "DrawByInsufficientMaterial" | "DrawByFiftyMove" | "Abandoned" | "DrawByAgreement" | "ServerRestart" | "NoShow";
            ratingPool: null | string;
            isRated: boolean;
            /** Format: int32 */
            plyCount: number;
            startFen: string;
            movesUci: string[];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            endedAt: null | string;
            /** Format: int32 */
            disconnectCount: number;
        };
        AccountExportProfileDto: {
            /** Format: uuid */
            userId: string;
            email: null | string;
            emailConfirmed: boolean;
            displayName: string;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            displayNameChangedAt: null | string;
            /** Format: date-time */
            termsAcceptedAt: null | string;
            termsVersion: null | string;
            /** Format: date-time */
            ageConfirmedAt: null | string;
            twoFactorEnabled: boolean;
            /** Format: date-time */
            pairingFoundEmailUnsubscribedAt: null | string;
            /** Format: date-time */
            rematchOfferedEmailUnsubscribedAt: null | string;
            isPatron: boolean;
            /** @enum {string} */
            patronTier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            /** @enum {string} */
            patronKind: "None" | "OneTime" | "Monthly";
            /** Format: date-time */
            patronSince: null | string;
            /** Format: date-time */
            patronExpiresAt: null | string;
            roles: string[];
            avatarJpegBase64: null | string;
        };
        AccountExportRatingDto: {
            pool: string;
            /** Format: double */
            rating: number;
            /** Format: double */
            ratingDeviation: number;
            /** Format: double */
            volatility: number;
            /** Format: int32 */
            gamesPlayed: number;
            /** Format: date-time */
            updatedAt: string;
        };
        AccountExportRatingHistoryDto: {
            pool: string;
            /** Format: uuid */
            gameId: string;
            /** Format: double */
            rating: number;
            /** Format: double */
            ratingDeviation: number;
            /** Format: date-time */
            createdAt: string;
        };
        AccountExportReportAgainstDto: {
            /** Format: uuid */
            reportId: string;
            /** Format: uuid */
            gameId: null | string;
            /** @enum {string} */
            reason: "EngineAssistance" | "Sandbagging" | "Stalling" | "Abuse" | "Other";
            /** @enum {string} */
            status: "Open" | "Reviewed" | "Dismissed";
            /** Format: date-time */
            createdAt: string;
        };
        AccountExportReportDto: {
            /** Format: uuid */
            reportId: string;
            /** Format: uuid */
            gameId: null | string;
            /** @enum {string} */
            reason: "EngineAssistance" | "Sandbagging" | "Stalling" | "Abuse" | "Other";
            details: null | string;
            /** @enum {string} */
            status: "Open" | "Reviewed" | "Dismissed";
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            resolvedAt: null | string;
        };
        AccountExportSanctionDto: {
            /** Format: uuid */
            sanctionId: string;
            /** @enum {string} */
            type: "Warning" | "RatedPlayBan" | "AccountBan";
            reason: string;
            /** Format: date-time */
            issuedAt: string;
            /** Format: date-time */
            expiresAt: null | string;
            /** Format: date-time */
            liftedAt: null | string;
        };
        AccountExportSessionDto: {
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            expiresAt: string;
            /** Format: date-time */
            revokedAt: null | string;
        };
        AccountExportTruncationDto: {
            gamesTruncated: boolean;
            /** Format: int32 */
            gamesCap: number;
            ratingHistoryTruncated: boolean;
            /** Format: int32 */
            ratingHistoryCap: number;
        };
        ApiTokenDto: {
            /** Format: uuid */
            id: string;
            name: string;
            tokenPrefix: string;
            scopes: string[];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            expiresAt: null | string;
            /** Format: date-time */
            lastUsedAt: null | string;
        };
        AuthResponse: {
            /** Format: uuid */
            userId: string;
            email: string;
            displayName: string;
            token: string;
            refreshToken: string;
            /** Format: date-time */
            expiresAt: string;
            roles: string[];
        };
        AvatarResponse: {
            avatarUrl: null | string;
        };
        BalancePoolDto: {
            pool: string;
            /** Format: int32 */
            games: number;
            /** Format: int32 */
            excludedGames: number;
            /** Format: double */
            whiteWinPercent: number;
            /** Format: double */
            blackWinPercent: number;
            /** Format: double */
            drawPercent: number;
            /** Format: double */
            checkmatePercent: number;
            /** Format: double */
            flagfallPercent: number;
            /** Format: double */
            avgPlies: number;
            /** Format: double */
            medianPlies: number;
            /** Format: double */
            avgDurationSeconds: number;
            /** Format: double */
            medianDurationSeconds: number;
            terminations: components["schemas"]["TerminationSliceDto"][];
        };
        BalanceSummaryDto: {
            /** Format: date-time */
            generatedAt: string;
            /** Format: int32 */
            sinceDays: number;
            ratedOnly: boolean;
            /** Format: int32 */
            totalGames: number;
            /** Format: int32 */
            excludedGames: number;
            truncated: boolean;
            overall: components["schemas"]["BalancePoolDto"];
            pools: components["schemas"]["BalancePoolDto"][];
        };
        ChallengeAcceptedDto: {
            /** Format: uuid */
            gameId: string;
            /** @enum {string} */
            yourColour: "White" | "Black";
            pool: string;
        };
        ChallengeAcceptedEvent: {
            token: string;
            /** Format: uuid */
            gameId: string;
            /** @enum {string} */
            yourColour: "White" | "Black";
            pool: string;
        };
        ChallengeDto: {
            token: string;
            challengerName: string;
            pool: string;
            /** @enum {string} */
            colour: "White" | "Black" | "Random";
            /** @enum {string} */
            status: "Open" | "Accepted" | "Cancelled" | "Expired";
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            expiresAt: string;
            /** Format: uuid */
            gameId: null | string;
            economy: components["schemas"]["EconomyDto"];
            challengerIsBot: boolean;
        };
        ChangeDisplayNameRequest: {
            displayName: string;
        };
        ClockEventDto: {
            /** @enum {string} */
            type: "CaptureGain" | "CaptureLoss" | "MovementToll" | "CheckGain" | "CheckLoss" | "PromotionCost" | "CastleCost" | "Increment" | "RepetitionNullified" | "FlagFall";
            /** @enum {string} */
            side: "White" | "Black";
            /** Format: int32 */
            amount: number;
            detail: null | string;
        };
        CohortRowDto: {
            /** Format: date */
            cohortStart: string;
            /** Format: int32 */
            signups: number;
            /** Format: int32 */
            playedFirstGame: number;
            /** Format: int32 */
            playedSecondGame: number;
            retention: components["schemas"]["RetentionPointDto"][];
        };
        ConfirmEmailRequest: {
            /** Format: uuid */
            userId: string;
            token: string;
        };
        ConfirmEmailResponse: {
            confirmed: boolean;
        };
        CounterCountDto: {
            key: string;
            /** Format: int64 */
            count: number;
        };
        CounterRequest: {
            key: null | string;
            source: null | string;
        };
        CreateApiTokenRequest: {
            name: string;
            scopes: string[];
            password: null | string;
            /** Format: int32 */
            expiresInDays?: null | number;
        };
        CreateChallengeRequest: {
            pool: null | string;
            /** @enum {string} */
            colour: "White" | "Black" | "Random";
            custom?: null | components["schemas"]["CustomConfigDto"];
        };
        CreatedApiTokenDto: {
            /** Format: uuid */
            id: string;
            name: string;
            tokenPrefix: string;
            scopes: string[];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            expiresAt: null | string;
            /** Format: date-time */
            lastUsedAt: null | string;
            token: string;
        };
        CreateGameRequest: {
            /** Format: uuid */
            opponentId: null | string;
            preset: null | string;
            rated: boolean;
            custom?: null | components["schemas"]["CustomConfigDto"];
        };
        CreateReportRequest: {
            /** Format: uuid */
            reportedUserId: string;
            /** Format: uuid */
            gameId: null | string;
            /** @enum {string} */
            reason: "EngineAssistance" | "Sandbagging" | "Stalling" | "Abuse" | "Other";
            details: null | string;
        };
        CreateSanctionRequest: {
            /** Format: uuid */
            userId: string;
            /** @enum {string} */
            type: "Warning" | "RatedPlayBan" | "AccountBan";
            reason: string;
            moderatorNote: null | string;
            /** Format: date-time */
            expiresAt: null | string;
            /** Format: uuid */
            reportId: null | string;
        };
        CurrentGameResponse: {
            /** Format: uuid */
            gameId: string;
            pool: null | string;
            solo: boolean;
        };
        CustomConfigDto: {
            /** Format: int32 */
            startTimeSeconds: number;
            /** Format: int32 */
            baseIncrement: number;
            /** Format: int32 */
            economyScale: number;
            pieceValues: components["schemas"]["PieceInts5Dto"];
            /** Format: int32 */
            captureMult: number;
            captureCaptorGainEnabled: boolean;
            captureVictimLossEnabled: boolean;
            /** @enum {string} */
            moveCostMode: "None" | "Flat" | "PerPiece" | "Distance" | "PerPieceXDistance";
            moveCostPerPiece: components["schemas"]["PieceInts6Dto"];
            /** Format: int32 */
            castleCostFlat: number;
            /** Format: int32 */
            moveCostFlat: number;
            /** Format: int32 */
            moveCostPerSquare: number;
            /** Format: int32 */
            checkCheckerGain: number;
            /** Format: int32 */
            checkVictimLoss: number;
            /** Format: int32 */
            doubleCheckChipMultiplier: number;
            /** Format: int32 */
            promotionCostMult: number;
            repeatedPositionNullifiesTime: boolean;
            threefoldRepetitionIsDraw: boolean;
            flagFallLoses: boolean;
            flagVsInsufficientMaterialIsDraw: boolean;
        };
        DeleteAccountRequest: {
            acknowledgement: string;
            password: null | string;
        };
        DeleteAccountResponse: {
            /** Format: date-time */
            deletedAt: string;
        };
        DetectionReportDto: {
            /** Format: uuid */
            userId: string;
            displayName: string;
            detector: string;
            pool: null | string;
            /** @enum {string} */
            verdict: "Insufficient" | "Unremarkable" | "Watch" | "Elevated";
            /** Format: double */
            score: number;
            /** Format: int32 */
            sampleSize: number;
            signals: components["schemas"]["DetectionSignalDto"][];
            caveats: string[];
            /** Format: date-time */
            computedAt: string;
        };
        DetectionSignalDto: {
            name: string;
            label: string;
            /** Format: double */
            value: number;
            /** Format: double */
            contribution: number;
            evidence: string;
        };
        DisplayNameResponse: {
            displayName: string;
            /** Format: date-time */
            changedAt: string;
        };
        DisplayNameScanRowDto: {
            /** Format: uuid */
            userId: string;
            displayName: string;
            storedNormalized: string;
            recomputedNormalized: string;
            normalizedDrift: boolean;
            rejection: null | string;
            collidesWithDisplayNames: string[];
            /** Format: date-time */
            createdAt: string;
        };
        DisplayNameVerdict: {
            allowed: boolean;
            rejection: null | string;
            message: string;
            normalized: string;
            /** Format: int32 */
            lengthLimit?: null | number;
        };
        DonationReferenceDto: {
            reference: string;
            /** Format: date-time */
            expiresAt: string;
        };
        DormantAccountRowDto: {
            /** Format: uuid */
            userId: string;
            displayName: string;
            /** @enum {string} */
            reason: "never-activated" | "never-played" | "inactive";
            /** Format: date-time */
            createdAt: string;
            emailConfirmed: boolean;
            /** Format: int32 */
            gamesPlayed: number;
            /** Format: date-time */
            lastGameAt: null | string;
            hasSanctions: boolean;
        };
        DormantAccountScanDto: {
            /** Format: date-time */
            generatedAt: string;
            /** Format: int32 */
            inactiveDays: number;
            /** Format: int32 */
            totalAccounts: number;
            /** Format: int32 */
            dormantCount: number;
            truncated: boolean;
            rows: components["schemas"]["DormantAccountRowDto"][];
        };
        DrawOfferedDto: {
            /** @enum {string} */
            bySide: "White" | "Black";
        };
        EconomyDto: {
            /** Format: int32 */
            startTimeSeconds: number;
            /** Format: int32 */
            economyScale: number;
            pieceValues: components["schemas"]["PieceInts5Dto"];
            /** Format: int32 */
            captureMult: number;
            captureCaptorGainEnabled: boolean;
            captureVictimLossEnabled: boolean;
            /** @enum {string} */
            moveCostMode: "None" | "Flat" | "PerPiece" | "Distance" | "PerPieceXDistance";
            moveCostPerPiece: components["schemas"]["PieceInts6Dto"];
            /** Format: int32 */
            castleCostFlat: number;
            /** Format: int32 */
            moveCostFlat: number;
            /** Format: int32 */
            moveCostPerSquare: number;
            /** Format: int32 */
            promotionCostMult: number;
            /** Format: int32 */
            checkCheckerGain: number;
            /** Format: int32 */
            checkVictimLoss: number;
            /** Format: int32 */
            doubleCheckChipMultiplier: number;
            /** Format: int32 */
            baseIncrement: number;
            repeatedPositionNullifiesTime: boolean;
            threefoldRepetitionIsDraw: boolean;
            flagFallLoses: boolean;
            flagVsInsufficientMaterialIsDraw: boolean;
        };
        EmailStatusDto: {
            email: null | string;
            confirmed: boolean;
            playBlocked: boolean;
        };
        EraseAccountRequest: {
            acknowledgement: string;
        };
        ErrorResponse: {
            error: string;
        };
        ExternalExchangeRequest: {
            code: string;
        };
        ExternalExchangeResponse: {
            /** Format: uuid */
            userId: string;
            email: string;
            displayName: string;
            token: string;
            refreshToken: string;
            /** Format: date-time */
            expiresAt: string;
            roles: string[];
            created: boolean;
        };
        FirstSessionDto: {
            /** Format: int64 */
            learnStarted: number;
            /** Format: int64 */
            learnCompleted: number;
            /** Format: int64 */
            learnSkipped: number;
            /** Format: int64 */
            queueJoined: number;
            /** Format: int64 */
            queueMatched: number;
            /** Format: int64 */
            gamesStarted: number;
            /** Format: int64 */
            gamesFinished: number;
            /** Format: double */
            learnCompletionPercent: null | number;
            /** Format: double */
            queueMatchPercent: null | number;
            /** Format: double */
            gameFinishPercent: null | number;
        };
        ForgotPasswordRequest: {
            email: string;
        };
        FunnelStepDto: {
            step: string;
            label: string;
            /** Format: int32 */
            count: number;
            /** Format: int32 */
            eligible: number;
            /** Format: double */
            percentOfSignups: null | number;
            /** Format: double */
            percentOfPrevious: null | number;
        };
        GameEndedDto: {
            state: components["schemas"]["GameStateResponse"];
            ratingChange?: {
                white: {
                    /** Format: double */
                    before: number;
                    /** Format: double */
                    after: number;
                };
                black: {
                    /** Format: double */
                    before: number;
                    /** Format: double */
                    after: number;
                };
            };
        };
        GameHistoryEntryDto: {
            /** Format: uuid */
            gameId: string;
            /** Format: uuid */
            opponentId: string;
            opponentName: string;
            /** @enum {string} */
            mySide: "White" | "Black";
            /** @enum {string} */
            result: "Ongoing" | "WhiteWins" | "BlackWins" | "Draw" | "Aborted";
            /** @enum {string} */
            termination: "None" | "Checkmate" | "Flagfall" | "Stalemate" | "Resignation" | "DrawByRepetition" | "DrawByInsufficientMaterial" | "DrawByFiftyMove" | "Abandoned" | "DrawByAgreement" | "ServerRestart" | "NoShow";
            ratingPool: null | string;
            isRated: boolean;
            /** Format: int32 */
            plyCount: number;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            endedAt: null | string;
            /** Format: double */
            ratingBefore: null | number;
            /** Format: double */
            ratingAfter: null | number;
        };
        GameSnapshotDto: {
            state: components["schemas"]["GameStateResponse"];
            legalMoves: string[];
            moves: components["schemas"]["SpectatorMoveDto"][];
        };
        GameStateResponse: {
            /** Format: uuid */
            gameId: string;
            fen: string;
            /** @enum {string} */
            sideToMove: "White" | "Black";
            /** Format: int32 */
            whiteTime: number;
            /** Format: int32 */
            blackTime: number;
            /** @enum {string} */
            status: "Ongoing" | "Checkmate" | "Stalemate" | "FlagFall" | "DrawByRepetition" | "DrawByInsufficientMaterial" | "DrawByFiftyMove" | "Aborted";
            /** @enum {null|string} */
            winner: "White" | "Black" | null;
            /** @enum {string} */
            termination: "None" | "Checkmate" | "Flagfall" | "Stalemate" | "Resignation" | "DrawByRepetition" | "DrawByInsufficientMaterial" | "DrawByFiftyMove" | "Abandoned" | "DrawByAgreement" | "ServerRestart" | "NoShow";
            /** Format: uuid */
            whitePlayerId: string;
            /** Format: uuid */
            blackPlayerId: string;
            /** Format: int32 */
            moveCount: number;
            economy: components["schemas"]["EconomyDto"];
            rated: boolean;
            /** Format: int32 */
            activeClockOffsetMs: number;
            /** Format: uuid */
            rematchOfferedById?: null | string;
            /** @default false */
            whiteViaApi: boolean;
            /** @default false */
            blackViaApi: boolean;
        };
        HttpValidationProblemDetails: {
            type?: null | string;
            title?: null | string;
            /** Format: int32 */
            status?: null | number;
            detail?: null | string;
            instance?: null | string;
            errors?: {
                [key: string]: string[];
            };
        };
        /** Format: binary */
        IFormFile: string;
        LeaderboardEntryDto: {
            /** Format: int32 */
            rank: number;
            /** Format: uuid */
            userId: string;
            displayName: string;
            /** Format: double */
            rating: number;
            /** Format: double */
            ratingDeviation: number;
            /** Format: int32 */
            gamesPlayed: number;
            provisional: boolean;
            /** @enum {string} */
            patronTier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            avatarUrl: null | string;
        };
        LiftSanctionRequest: {
            reason: string;
        };
        LiveGameDto: {
            /** Format: uuid */
            gameId: string;
            whiteName: string;
            blackName: string;
            /** @enum {string} */
            whitePatronTier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            /** @enum {string} */
            blackPatronTier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            pool: null | string;
            rated: boolean;
            /** Format: int32 */
            whiteTime: number;
            /** Format: int32 */
            blackTime: number;
            /** @enum {string} */
            sideToMove: "White" | "Black";
            /** Format: int32 */
            moveCount: number;
            /** Format: int32 */
            spectators: number;
            /** Format: date-time */
            startedAt: string;
            fen?: null | string;
        };
        LoginRequest: {
            email: string;
            password: string;
        };
        LogoutRequest: {
            refreshToken: string;
        };
        MatchFoundDto: {
            /** Format: uuid */
            gameId: string;
            /** @enum {string} */
            color: "White" | "Black";
            pool: string;
        };
        ModerationRatingDto: {
            pool: string;
            /** Format: double */
            value: number;
            /** Format: double */
            ratingDeviation: number;
            /** Format: int32 */
            gamesPlayed: number;
            isProvisional: boolean;
        };
        ModerationReportDto: {
            /** Format: uuid */
            id: string;
            /** Format: uuid */
            reporterId: string;
            reporterDisplayName: string;
            /** Format: uuid */
            reportedUserId: string;
            reportedUserDisplayName: string;
            /** Format: uuid */
            gameId: null | string;
            /** @enum {string} */
            reason: "EngineAssistance" | "Sandbagging" | "Stalling" | "Abuse" | "Other";
            details: null | string;
            /** @enum {string} */
            status: "Open" | "Reviewed" | "Dismissed";
            moderatorNote: null | string;
            /** Format: uuid */
            reviewedByUserId: null | string;
            /** Format: date-time */
            reviewedAt: null | string;
            /** Format: date-time */
            createdAt: string;
            /** Format: int32 */
            openReportsAgainstReportedUser: number;
        };
        ModerationUserDto: {
            /** Format: uuid */
            userId: string;
            displayName: string;
            email: string;
            /** Format: date-time */
            createdAt: string;
            emailConfirmed: boolean;
            ratings: components["schemas"]["ModerationRatingDto"][];
            /** Format: int32 */
            gamesPlayed: number;
            /** Format: int32 */
            gamesAbandoned: number;
            /** Format: int32 */
            reportsAgainst: number;
            /** Format: int32 */
            reportsFiled: number;
            sanctions: components["schemas"]["SanctionDto"][];
            /** Format: int32 */
            graceDisconnects: number;
        };
        ModeratorRenameRequest: {
            displayName: string;
            reason: string;
        };
        MovePlayedDto: {
            state: components["schemas"]["GameStateResponse"];
            uci: string;
            /** Format: int32 */
            whiteDelta: number;
            /** Format: int32 */
            blackDelta: number;
            events: components["schemas"]["ClockEventDto"][];
            legalMoves: string[];
            gameOver: boolean;
            ratingChange?: {
                white: {
                    /** Format: double */
                    before: number;
                    /** Format: double */
                    after: number;
                };
                black: {
                    /** Format: double */
                    before: number;
                    /** Format: double */
                    after: number;
                };
            };
        };
        MoveRequest: {
            uci: string;
        };
        MoveResponse: {
            state: components["schemas"]["GameStateResponse"];
            /** Format: int32 */
            whiteDelta: number;
            /** Format: int32 */
            blackDelta: number;
            events: components["schemas"]["ClockEventDto"][];
            gameOver: boolean;
        };
        MySanctionDto: {
            /** @enum {string} */
            type: "Warning" | "RatedPlayBan" | "AccountBan";
            reason: string;
            /** Format: date-time */
            issuedAt: string;
            /** Format: date-time */
            expiresAt: null | string;
        };
        NotificationCategoryPreferenceDto: {
            /** @enum {string} */
            category: "PairingFound" | "RematchOffered";
            subscribed: boolean;
        };
        NotificationPreferencesDto: {
            categories: components["schemas"]["NotificationCategoryPreferenceDto"][];
        };
        OpenSeekDto: {
            pool: string;
            /** Format: date-time */
            waitingSinceUtc: string;
        };
        OpponentDisconnectedDto: {
            /** Format: int32 */
            graceSeconds: number;
        };
        PatronStatusDto: {
            isPatron: boolean;
            /** @enum {string} */
            tier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            /** @enum {string} */
            kind: "None" | "OneTime" | "Monthly";
            /** Format: date-time */
            since: null | string;
            /** Format: date-time */
            expiresAt: null | string;
        };
        PieceInts5Dto: {
            /** Format: int32 */
            pawn: number;
            /** Format: int32 */
            knight: number;
            /** Format: int32 */
            bishop: number;
            /** Format: int32 */
            rook: number;
            /** Format: int32 */
            queen: number;
        };
        PieceInts6Dto: {
            /** Format: int32 */
            pawn: number;
            /** Format: int32 */
            knight: number;
            /** Format: int32 */
            bishop: number;
            /** Format: int32 */
            rook: number;
            /** Format: int32 */
            queen: number;
            /** Format: int32 */
            king: number;
        };
        PresenceDto: {
            /** Format: int32 */
            online: number;
            /** Format: int32 */
            inGame: number;
        };
        PublicProfileDto: {
            /** Format: uuid */
            userId: string;
            displayName: string;
            /** Format: date-time */
            joinedAt: string;
            /** @enum {string} */
            patronTier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            /** Format: int32 */
            gamesPlayed: number;
            ratings: components["schemas"]["RatingDto"][];
            avatarUrl: null | string;
        };
        PushSubscribeRequest: {
            endpoint: string;
            p256dh: string;
            auth: string;
        };
        PushUnsubscribeRequest: {
            endpoint: string;
        };
        RatingDto: {
            pool: string;
            /** Format: double */
            rating: number;
            /** Format: double */
            ratingDeviation: number;
            /** Format: double */
            volatility: number;
            /** Format: int32 */
            gamesPlayed: number;
            provisional: boolean;
            /** Format: date-time */
            updatedAt: string;
        };
        RatingHistoryPointDto: {
            /** Format: uuid */
            gameId: string;
            /** Format: double */
            valueBefore: number;
            /** Format: double */
            valueAfter: number;
            /** Format: double */
            rdAfter: number;
            /** Format: date-time */
            createdAt: string;
        };
        RefreshRequest: {
            refreshToken: string;
        };
        RegisterRequest: {
            email: string;
            password: string;
            displayName: string;
            acceptedTerms: boolean;
            confirmedMinimumAge: boolean;
            turnstileToken?: null | string;
        };
        RematchAcceptedDto: {
            /** Format: uuid */
            gameId: string;
            /** Format: uuid */
            whitePlayerId: string;
            /** Format: uuid */
            blackPlayerId: string;
            pool: null | string;
            rated: boolean;
        };
        ReplayPlyDto: {
            /** Format: int32 */
            ply: number;
            uci: string;
            san: string;
            /** @enum {string} */
            mover: "White" | "Black";
            fenAfter: string;
            /** Format: int32 */
            whiteTimeAfter: number;
            /** Format: int32 */
            blackTimeAfter: number;
            /** Format: int32 */
            whiteDelta: number;
            /** Format: int32 */
            blackDelta: number;
            events: components["schemas"]["ClockEventDto"][];
        };
        ReplayResponse: {
            /** Format: uuid */
            gameId: string;
            startFen: string;
            /** Format: uuid */
            whitePlayerId: string;
            /** Format: uuid */
            blackPlayerId: string;
            whitePlayerName: string;
            blackPlayerName: string;
            /** @enum {string} */
            whitePatronTier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            /** @enum {string} */
            blackPatronTier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            /** @enum {string} */
            result: "Ongoing" | "WhiteWins" | "BlackWins" | "Draw" | "Aborted";
            /** @enum {string} */
            termination: "None" | "Checkmate" | "Flagfall" | "Stalemate" | "Resignation" | "DrawByRepetition" | "DrawByInsufficientMaterial" | "DrawByFiftyMove" | "Abandoned" | "DrawByAgreement" | "ServerRestart" | "NoShow";
            ratingPool: null | string;
            isRated: boolean;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            endedAt: null | string;
            economy: components["schemas"]["EconomyDto"];
            plies: components["schemas"]["ReplayPlyDto"][];
            /** Format: int32 */
            truncatedAtPly: null | number;
        };
        ReportDto: {
            /** Format: uuid */
            id: string;
            /** Format: uuid */
            reportedUserId: string;
            /** Format: uuid */
            gameId: null | string;
            /** @enum {string} */
            reason: "EngineAssistance" | "Sandbagging" | "Stalling" | "Abuse" | "Other";
            details: null | string;
            /** @enum {string} */
            status: "Open" | "Reviewed" | "Dismissed";
            /** Format: date-time */
            createdAt: string;
        };
        ResendConfirmationRequest: {
            email: string;
        };
        ResetPasswordRequest: {
            email: string;
            token: string;
            newPassword: string;
        };
        ResetPasswordResponse: {
            reset: boolean;
        };
        ResolveReportRequest: {
            /** @enum {string} */
            status: "Open" | "Reviewed" | "Dismissed";
            moderatorNote: null | string;
        };
        RetentionPointDto: {
            /** Format: int32 */
            day: number;
            /** Format: int32 */
            eligible: number;
            /** Format: int32 */
            retained: number;
            /** Format: double */
            percent: null | number;
        };
        RetentionSummaryDto: {
            /** Format: date-time */
            generatedAt: string;
            /** Format: int32 */
            sinceDays: number;
            grain: string;
            truncated: boolean;
            /** Format: int32 */
            totalSignups: number;
            funnel: components["schemas"]["FunnelStepDto"][];
            retention: components["schemas"]["RetentionPointDto"][];
            cohorts: components["schemas"]["CohortRowDto"][];
        };
        SanctionDto: {
            /** Format: uuid */
            id: string;
            /** Format: uuid */
            userId: string;
            userDisplayName: string;
            /** @enum {string} */
            type: "Warning" | "RatedPlayBan" | "AccountBan";
            reason: string;
            moderatorNote: null | string;
            /** Format: uuid */
            issuedByUserId: string;
            issuedByDisplayName: string;
            /** Format: date-time */
            issuedAt: string;
            /** Format: date-time */
            expiresAt: null | string;
            /** Format: date-time */
            liftedAt: null | string;
            /** Format: uuid */
            liftedByUserId: null | string;
            liftReason: null | string;
            /** Format: uuid */
            reportId: null | string;
            isActive: boolean;
        };
        SentResponse: {
            sent: boolean;
        };
        SessionDto: {
            /** Format: uuid */
            id: string;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            expiresAt: string;
            isCurrent: boolean;
        };
        SetKillSwitchRequest: {
            enabled: boolean;
        };
        SourceCountDto: {
            source: string;
            /** Format: int64 */
            pageViews: number;
            /** Format: int64 */
            signups: number;
        };
        SpectatorMoveDto: {
            /** Format: int32 */
            ply: number;
            uci: string;
            /** @enum {string} */
            mover: "White" | "Black";
            /** Format: int32 */
            whiteTimeAfter: number;
            /** Format: int32 */
            blackTimeAfter: number;
            /** Format: int32 */
            whiteDelta: number;
            /** Format: int32 */
            blackDelta: number;
        };
        SpectatorsChangedDto: {
            /** Format: int32 */
            count: number;
        };
        SpectatorSnapshotDto: {
            state: components["schemas"]["GameStateResponse"];
            whiteName: string;
            blackName: string;
            /** @enum {string} */
            whitePatronTier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            /** @enum {string} */
            blackPatronTier: "None" | "Pawn" | "Knight" | "Bishop" | "Rook" | "Queen";
            moves: components["schemas"]["SpectatorMoveDto"][];
            /** Format: int32 */
            spectators: number;
        };
        TerminationSliceDto: {
            /** @enum {string} */
            reason: "None" | "Checkmate" | "Flagfall" | "Stalemate" | "Resignation" | "DrawByRepetition" | "DrawByInsufficientMaterial" | "DrawByFiftyMove" | "Abandoned" | "DrawByAgreement" | "ServerRestart" | "NoShow";
            /** Format: int32 */
            count: number;
            /** Format: double */
            percent: number;
        };
        TrafficDayDto: {
            /** Format: date */
            date: string;
            /** Format: int64 */
            pageViews: number;
            /** Format: int32 */
            signups: number;
            /** Format: int64 */
            gamesStarted: number;
            /** Format: int64 */
            gamesFinished: number;
            pages: components["schemas"]["CounterCountDto"][];
        };
        TrafficSummaryDto: {
            /** Format: date-time */
            generatedAt: string;
            /** Format: int32 */
            sinceDays: number;
            /** Format: int64 */
            totalPageViews: number;
            /** Format: int32 */
            totalSignups: number;
            /** Format: double */
            signupsPerHundredLandingViews: null | number;
            truncated: boolean;
            byPage: components["schemas"]["CounterCountDto"][];
            bySource: components["schemas"]["SourceCountDto"][];
            firstSession: components["schemas"]["FirstSessionDto"];
            days: components["schemas"]["TrafficDayDto"][];
        };
        TwoFactorCodeRequest: {
            code: string;
        };
        TwoFactorDisableRequest: {
            code: string;
            password: null | string;
        };
        TwoFactorDisableResponse: {
            auth: components["schemas"]["AuthResponse"];
        };
        TwoFactorEnableRequest: {
            code: string;
            password: null | string;
            googleReauthCode: null | string;
        };
        TwoFactorEnableResponse: {
            recoveryCodes: string[];
            auth: components["schemas"]["AuthResponse"];
        };
        TwoFactorEnrolmentResponse: {
            sharedKey: string;
            otpauthUri: string;
        };
        TwoFactorLoginRequest: {
            ticket: string;
            code: string;
        };
        TwoFactorRecoveryCodesResponse: {
            recoveryCodes: string[];
        };
        TwoFactorRequiredResponse: {
            twoFactorRequired: boolean;
            twoFactorTicket: string;
            /** Format: int32 */
            expiresInSeconds: number;
        };
        TwoFactorStatusResponse: {
            enabled: boolean;
            /** Format: int32 */
            recoveryCodesRemaining: number;
            hasPassword: boolean;
        };
        UnsubscribeRequest: {
            /** Format: uuid */
            userId: string;
            /** @enum {string} */
            category: "PairingFound" | "RematchOffered";
            token: string;
        };
        UpdateNotificationPreferenceRequest: {
            /** @enum {string} */
            category: "PairingFound" | "RematchOffered";
            subscribed: boolean;
        };
        VapidPublicKeyDto: {
            enabled: boolean;
            publicKey: null | string;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export type operations = Record<string, never>;
