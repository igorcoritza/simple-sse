import { environment } from '../environments/environment';

// Base URL of the .NET backend ('' in production = same origin, behind the ingress).
export const API_URL = environment.apiUrl;
