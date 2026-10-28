namespace CellRelay;
static class WindowCloseTests
{
    public static async Task Run(){
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            try{
                using(var form=new MainForm {ShowInTaskbar=false,Opacity=0}){
                    form.Shown+=(_,_)=>form.Close();
                    Application.Run(form);
                }
                completed.TrySetResult();
            }catch(Exception e){completed.TrySetException(e);}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
