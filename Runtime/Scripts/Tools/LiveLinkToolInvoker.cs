using System;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiveLink.Tools
{
    public static class LiveLinkToolInvoker
    {
        public static async Task<object> InvokeAsync(LiveLinkToolDescriptor descriptor, JObject arguments)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            object[] parameters = BindParameters(descriptor, arguments);

            if (descriptor.RequiresMainThread && !MainThreadDispatcher.IsMainThread)
            {
                return await InvokeOnMainThreadAsync(descriptor, parameters).ConfigureAwait(false);
            }

            return await InvokeCoreAsync(descriptor, parameters).ConfigureAwait(false);
        }

        private static object[] BindParameters(LiveLinkToolDescriptor descriptor, JObject arguments)
        {
            var bound = new object[descriptor.Parameters.Count];
            JObject safeArguments = arguments ?? new JObject();

            for (int i = 0; i < descriptor.Parameters.Count; i++)
            {
                LiveLinkToolParameterDescriptor parameter = descriptor.Parameters[i];
                JToken token;

                if (!safeArguments.TryGetValue(parameter.Name, StringComparison.OrdinalIgnoreCase, out token))
                {
                    if (parameter.HasDefaultValue)
                    {
                        bound[i] = parameter.DefaultValue;
                        continue;
                    }

                    if (parameter.Required)
                    {
                        throw new ArgumentException("Missing required argument: " + parameter.Name);
                    }

                    bound[i] = GetDefault(parameter.ParameterType);
                    continue;
                }

                bound[i] = ConvertToken(token, parameter.ParameterType, parameter.Name);
            }

            return bound;
        }

        private static object ConvertToken(JToken token, Type targetType, string parameterName)
        {
            try
            {
                Type nonNullableTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;

                if (nonNullableTarget.IsEnum)
                {
                    if (token.Type == JTokenType.String)
                    {
                        return Enum.Parse(nonNullableTarget, token.ToString(), true);
                    }

                    object enumNumeric = token.ToObject(Enum.GetUnderlyingType(nonNullableTarget));
                    return Enum.ToObject(nonNullableTarget, enumNumeric);
                }

                if (token.Type == JTokenType.Null)
                {
                    return null;
                }

                return token.ToObject(nonNullableTarget);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Invalid argument '" + parameterName + "': " + ex.Message, ex);
            }
        }

        private static object GetDefault(Type type)
        {
            if (!type.IsValueType)
            {
                return null;
            }

            return Activator.CreateInstance(type);
        }

        private static async Task<object> InvokeOnMainThreadAsync(LiveLinkToolDescriptor descriptor, object[] parameters)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainThreadDispatcher.Enqueue(() =>
            {
                _ = InvokeOnMainThreadInnerAsync(descriptor, parameters, tcs);
            });

            return await tcs.Task.ConfigureAwait(false);
        }

        private static async Task InvokeOnMainThreadInnerAsync(LiveLinkToolDescriptor descriptor, object[] parameters, TaskCompletionSource<object> tcs)
        {
            try
            {
                object result = await InvokeCoreAsync(descriptor, parameters).ConfigureAwait(false);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        private static async Task<object> InvokeCoreAsync(LiveLinkToolDescriptor descriptor, object[] parameters)
        {
            MethodInfo method = descriptor.Method;
            object returnValue = method.Invoke(descriptor.TargetInstance, parameters);

            if (returnValue == null)
            {
                return null;
            }

            if (returnValue is Task task)
            {
                await task.ConfigureAwait(false);

                Type taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    PropertyInfo resultProperty = taskType.GetProperty("Result");
                    if (resultProperty != null)
                    {
                        return resultProperty.GetValue(task, null);
                    }
                }

                return null;
            }

            return returnValue;
        }
    }
}
